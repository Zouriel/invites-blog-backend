using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Faces;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public sealed class FaceGroupingServiceTests : IDisposable
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);

    public void Dispose() => _db.Dispose();

    private FaceGroupingService Service(bool available = true)
    {
        var analyzer = Substitute.For<IFaceAnalyzer>();
        analyzer.Available.Returns(available);
        return new FaceGroupingService(
            new BaseRepository<MediaBucket>(_db),
            new BaseRepository<PhotoFace>(_db),
            new BaseRepository<FacePerson>(_db),
            new BaseRepository<FaceJob>(_db),
            new BaseRepository<EventPhoto>(_db),
            new UnitOfWork(_db),
            analyzer);
    }

    private static MediaBucket Bucket(bool groupingOn = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new MediaBucket
        {
            Id = Guid.NewGuid(),
            OwnerUserId = Guid.NewGuid(),
            CampaignId = Guid.NewGuid(),
            EventDate = now,
            FaceGroupingOn = groupingOn,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static EventPhoto Photo(MediaBucket bucket, string contentType = "image/jpeg", bool deleted = false)
    {
        var id = Guid.NewGuid();
        return new EventPhoto
        {
            Id = id,
            BucketId = bucket.Id,
            ContentType = contentType,
            Url = "u/" + id,
            ThumbUrl = "t/" + id,
            OriginalUrl = "o/" + id,
            CreatedAt = DateTimeOffset.UtcNow,
            DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
        };
    }

    private static FacePerson Person(MediaBucket bucket, string? name = null, bool ignored = false) => new()
    {
        Id = Guid.NewGuid(),
        BucketId = bucket.Id,
        Name = name,
        Ignored = ignored,
        Centroid = [1f, 0f],
        FaceCount = 1,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static PhotoFace Face(MediaBucket bucket, EventPhoto photo, FacePerson person,
        float w = .2f, float h = .2f, float score = .9f, float x = .1f, float y = .1f) => new()
    {
        Id = Guid.NewGuid(),
        BucketId = bucket.Id,
        PhotoId = photo.Id,
        PersonId = person.Id,
        X = x,
        Y = y,
        W = w,
        H = h,
        Score = score,
        Embedding = [1f, 0f],
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static FaceJob Job(MediaBucket bucket, EventPhoto photo, FaceJobStatus status = FaceJobStatus.Waiting) => new()
    {
        Id = photo.Id,
        BucketId = bucket.Id,
        Status = status,
        NextAttemptAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public void Match_returns_only_the_closest_group_at_or_above_the_threshold()
    {
        var bucket = Bucket();
        var lower = Person(bucket);
        lower.Centroid = [.6f, .8f];
        var higher = Person(bucket);
        higher.Centroid = [.8f, .6f];

        Assert.Same(higher, FaceClustering.Match([1f, 0f], [lower, higher], .5f));
        Assert.Null(FaceClustering.Match([1f, 0f], [lower, higher], .9f));
        Assert.Same(lower, FaceClustering.Match([1f, 0f], [lower], .6f));
    }

    [Fact]
    public void Add_increments_the_count_and_keeps_the_centroid_unit_length()
    {
        var group = Person(Bucket());
        var original = group.Centroid.ToArray();

        FaceClustering.Add(group, original);

        Assert.Equal(2, group.FaceCount);
        Assert.Equal(original, group.Centroid);

        FaceClustering.Add(group, [0f, 1f]);

        Assert.Equal(3, group.FaceCount);
        Assert.Equal(1f, MathF.Sqrt(group.Centroid.Sum(x => x * x)), 5);
    }

    [Fact]
    public async Task Turning_on_without_telling_guests_is_refused_without_changing_the_bucket()
    {
        var bucket = Bucket();
        _db.MediaBuckets.Add(bucket);
        await _db.SaveChangesAsync(Ct);

        var error = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Service().SetAsync(bucket, true, false, Guid.NewGuid(), Ct));

        Assert.Equal("faces_guests_not_told", error.ErrorCode);
        Assert.False(bucket.FaceGroupingOn);
        Assert.False((await _db.MediaBuckets.FindAsync([bucket.Id], Ct))!.FaceGroupingOn);
    }

    [Fact]
    public async Task Turning_on_when_the_analyzer_is_unavailable_is_refused()
    {
        var bucket = Bucket();

        await Assert.ThrowsAsync<InvalidStateException>(
            () => Service(available: false).SetAsync(bucket, true, true, Guid.NewGuid(), Ct));

        Assert.False(bucket.FaceGroupingOn);
    }

    [Fact]
    public async Task Turning_on_confirms_who_enabled_it_and_queues_each_live_picture_once_for_that_bucket()
    {
        var bucket = Bucket();
        var otherBucket = Bucket();
        var first = Photo(bucket);
        var alreadyQueued = Photo(bucket, "image/png");
        var video = Photo(bucket, "video/mp4");
        var deleted = Photo(bucket, deleted: true);
        var elsewhere = Photo(otherBucket);
        var by = Guid.NewGuid();
        _db.AddRange(bucket, otherBucket, first, alreadyQueued, video, deleted, elsewhere, Job(bucket, alreadyQueued));
        await _db.SaveChangesAsync(Ct);

        var before = DateTimeOffset.UtcNow;
        await Service().SetAsync(bucket, true, true, by, Ct);

        Assert.True(bucket.FaceGroupingOn);
        Assert.Equal(by, bucket.FaceGroupingConfirmedBy);
        Assert.NotNull(bucket.FaceGroupingConfirmedAt);
        Assert.InRange(bucket.FaceGroupingConfirmedAt!.Value, before, DateTimeOffset.UtcNow);
        var jobs = await _db.FaceJobs.OrderBy(j => j.Id).ToListAsync(Ct);
        Assert.Equal(2, jobs.Count);
        Assert.Equal(new[] { first.Id, alreadyQueued.Id }.Order(), jobs.Select(j => j.Id));
        Assert.All(jobs, j => Assert.Equal(bucket.Id, j.BucketId));
    }

    [Fact]
    public async Task Turning_off_removes_only_that_buckets_face_data_and_jobs()
    {
        var bucket = Bucket(groupingOn: true);
        var otherBucket = Bucket(groupingOn: true);
        var photo = Photo(bucket);
        var otherPhoto = Photo(otherBucket);
        var person = Person(bucket);
        var otherPerson = Person(otherBucket);
        _db.AddRange(bucket, otherBucket, photo, otherPhoto, person, otherPerson,
            Face(bucket, photo, person), Face(otherBucket, otherPhoto, otherPerson),
            Job(bucket, photo), Job(otherBucket, otherPhoto));
        await _db.SaveChangesAsync(Ct);

        await Service().SetAsync(bucket, false, false, null, Ct);

        Assert.False(bucket.FaceGroupingOn);
        Assert.DoesNotContain(await _db.PhotoFaces.ToListAsync(Ct), f => f.BucketId == bucket.Id);
        Assert.DoesNotContain(await _db.FacePeople.ToListAsync(Ct), p => p.BucketId == bucket.Id);
        Assert.DoesNotContain(await _db.FaceJobs.ToListAsync(Ct), j => j.BucketId == bucket.Id);
        Assert.Single(_db.PhotoFaces, f => f.BucketId == otherBucket.Id);
        Assert.Single(_db.FacePeople, p => p.BucketId == otherBucket.Id);
        Assert.Single(_db.FaceJobs, j => j.BucketId == otherBucket.Id);
    }

    [Fact]
    public async Task Queue_adds_only_pictures_from_buckets_with_grouping_on()
    {
        var on = Bucket(groupingOn: true);
        var off = Bucket();
        var picture = Photo(on);
        var video = Photo(on, "video/mp4");
        var offPicture = Photo(off);
        _db.AddRange(on, off, picture, video, offPicture);
        await _db.SaveChangesAsync(Ct);
        var service = Service();

        await service.QueueAsync(picture, Ct);
        await service.QueueAsync(video, Ct);
        await service.QueueAsync(offPicture, Ct);
        await new UnitOfWork(_db).SaveChangesAsync(Ct);

        var job = Assert.Single(await _db.FaceJobs.ToListAsync(Ct));
        Assert.Equal(picture.Id, job.Id);
        Assert.Equal(on.Id, job.BucketId);
    }

    [Fact]
    public async Task ForgetPhotos_removes_only_the_requested_photos_faces_and_job()
    {
        var bucket = Bucket(groupingOn: true);
        var forgotten = Photo(bucket);
        var kept = Photo(bucket);
        var person = Person(bucket);
        _db.AddRange(bucket, forgotten, kept, person,
            Face(bucket, forgotten, person), Face(bucket, kept, person),
            Job(bucket, forgotten), Job(bucket, kept));
        await _db.SaveChangesAsync(Ct);

        await Service().ForgetPhotosAsync([forgotten.Id], Ct);

        Assert.DoesNotContain(_db.PhotoFaces, f => f.PhotoId == forgotten.Id);
        Assert.DoesNotContain(_db.FaceJobs, j => j.Id == forgotten.Id);
        Assert.Single(_db.PhotoFaces, f => f.PhotoId == kept.Id);
        Assert.Single(_db.FaceJobs, j => j.Id == kept.Id);
        Assert.NotNull(await _db.FacePeople.FindAsync([person.Id], Ct));
    }

    [Fact]
    public async Task ForBox_builds_the_visible_groups_cover_mapping_order_and_waiting_count()
    {
        var bucket = Bucket();
        var photos = new[] { Photo(bucket), Photo(bucket), Photo(bucket) };
        _db.Add(bucket);
        _db.AddRange(photos);
        await _db.SaveChangesAsync(Ct);
        var service = Service();

        var off = await service.ForBoxAsync([bucket.Id], photos, true, Ct);
        Assert.Null(off.Dto);
        Assert.Empty(off.PeopleByPhoto);

        bucket.FaceGroupingOn = true;
        var namedMany = Person(bucket, "Alice");
        var namedOne = Person(bucket, "Bea");
        var unnamedMany = Person(bucket);
        var unnamedOne = Person(bucket);
        var ignored = Person(bucket, "Hidden", ignored: true);
        _db.AddRange(namedMany, namedOne, unnamedMany, unnamedOne, ignored);
        _db.AddRange(
            Face(bucket, photos[0], namedMany, .1f, .1f, .9f),
            Face(bucket, photos[1], namedMany, .4f, .4f, .5f, .3f, .2f),
            Face(bucket, photos[0], namedOne),
            Face(bucket, photos[0], unnamedMany),
            Face(bucket, photos[1], unnamedMany),
            Face(bucket, photos[2], unnamedMany),
            Face(bucket, photos[2], unnamedOne),
            Face(bucket, photos[0], ignored),
            Face(bucket, photos[1], ignored));
        _db.AddRange(
            Job(bucket, photos[0], FaceJobStatus.Waiting),
            Job(bucket, photos[1], FaceJobStatus.Running),
            Job(bucket, photos[2], FaceJobStatus.Failed));
        await _db.SaveChangesAsync(Ct);

        var (dto, peopleByPhoto) = await service.ForBoxAsync([bucket.Id], photos, false, Ct);

        Assert.NotNull(dto);
        Assert.True(dto.On);
        Assert.False(dto.CanName);
        Assert.Equal(2, dto.Waiting);
        Assert.Equal([namedMany.Id, namedOne.Id, unnamedMany.Id], dto.People.Select(p => p.Id).ToArray());
        Assert.DoesNotContain(dto.People, p => p.Id == unnamedOne.Id || p.Id == ignored.Id);
        var cover = dto.People.Single(p => p.Id == namedMany.Id);
        Assert.Equal(2, cover.PhotoCount);
        Assert.Equal(photos[1].ThumbUrl, cover.CoverUrl);
        Assert.Equal((.3f, .2f, .4f, .4f), (cover.CoverX, cover.CoverY, cover.CoverW, cover.CoverH));
        Assert.Equal(3, peopleByPhoto.Count);
        Assert.Equal([namedMany.Id, namedOne.Id, unnamedMany.Id, ignored.Id], peopleByPhoto[photos[0].Id]);
        Assert.Equal([namedMany.Id, unnamedMany.Id, ignored.Id], peopleByPhoto[photos[1].Id]);
        Assert.Equal([unnamedMany.Id, unnamedOne.Id], peopleByPhoto[photos[2].Id]);
    }

    [Fact]
    public async Task Ignore_marks_the_person_clears_the_name_and_only_deletes_their_faces()
    {
        var bucket = Bucket(groupingOn: true);
        var photo = Photo(bucket);
        var ignored = Person(bucket, "Alice");
        ignored.Centroid = [.6f, .8f];
        var kept = Person(bucket, "Bea");
        _db.AddRange(bucket, photo, ignored, kept, Face(bucket, photo, ignored), Face(bucket, photo, kept));
        await _db.SaveChangesAsync(Ct);
        var centroid = ignored.Centroid.ToArray();

        await Service().IgnoreAsync(ignored.Id, Ct);

        Assert.True(ignored.Ignored);
        Assert.Null(ignored.Name);
        Assert.Equal(centroid, ignored.Centroid);
        Assert.DoesNotContain(_db.PhotoFaces, f => f.PersonId == ignored.Id);
        Assert.Single(_db.PhotoFaces, f => f.PersonId == kept.Id);
    }

    [Fact]
    public async Task Name_trims_caps_and_clears_a_name()
    {
        var bucket = Bucket(groupingOn: true);
        var first = Photo(bucket);
        var second = Photo(bucket);
        var person = Person(bucket);
        _db.AddRange(bucket, first, second, person, Face(bucket, first, person), Face(bucket, second, person));
        await _db.SaveChangesAsync(Ct);
        var service = Service();

        var trimmed = await service.NameAsync(person.Id, "  Alice  ", [first, second], Ct);
        Assert.Equal("Alice", trimmed.Name);

        var capped = await service.NameAsync(person.Id, "  " + new string('x', FacePerson.MaxNameLength + 5) + "  ", [first, second], Ct);
        Assert.Equal(new string('x', FacePerson.MaxNameLength), capped.Name);

        var cleared = await service.NameAsync(person.Id, "   ", [first, second], Ct);
        Assert.Null(cleared.Name);
        Assert.Null((await _db.FacePeople.FindAsync([person.Id], Ct))!.Name);
    }
}
