using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Photos;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Faces;

/// <summary>
/// Face grouping in albums: switching it on and off, queueing photos, what a box shows of it, and naming and
/// opting out of groups. Finding the faces is <c>FaceIndexService</c> (in the API host, one photo at a time).
///
/// <para>Face data is biometric data, so every rule here is about not keeping it: only albums whose host
/// switched it on, only inside that album, deleted when the photo is, when the album is, when it's switched
/// off, and when someone opts out (then only the group's average stays, so new photos of them aren't grouped;
/// it goes with the album too). See docs/FUTURE-PLANS.md §9.</para>
/// </summary>
public interface IFaceGroupingService
{
    bool Available { get; }

    /// <summary>Switches it on (queueing every photo already there) or off (deleting every face, group and job). The caller has checked the host owns the album.</summary>
    Task SetAsync(MediaBucket bucket, bool on, bool guestsTold, Guid? by, CancellationToken ct);

    /// <summary>Queues a new photo when its album has grouping on. Added to the caller's unit of work; the caller saves.</summary>
    Task QueueAsync(EventPhoto photo, CancellationToken ct);

    /// <summary>Removes a photo's faces and job (the photo was removed). Saves.</summary>
    Task ForgetPhotosAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken ct);

    /// <summary>What a box shows: the groups and, per photo, who is in it. Null when no album among the photos has it on.</summary>
    Task<(FaceGroupingDto? Dto, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> PeopleByPhoto)> ForBoxAsync(
        IReadOnlyCollection<Guid> bucketIds, IReadOnlyList<EventPhoto> photos, bool canName, CancellationToken ct);

    /// <summary>Names a group (blank clears it).</summary>
    Task<FacePersonDto> NameAsync(Guid personId, string? name, IReadOnlyList<EventPhoto> photos, CancellationToken ct);

    /// <summary>"Ignore my face": deletes the group's faces and keeps it out of grouping.</summary>
    Task IgnoreAsync(Guid personId, CancellationToken ct);

    /// <summary>Whether this album groups faces: what the camera's notice asks.</summary>
    Task<bool> OnForBucketAsync(Guid bucketId, CancellationToken ct);

    /// <summary>Whether any album of this event groups faces.</summary>
    Task<bool> OnForCampaignAsync(Guid campaignId, CancellationToken ct);
}

public sealed class FaceGroupingService(
    IRepository<MediaBucket> buckets,
    IRepository<PhotoFace> faces,
    IRepository<FacePerson> people,
    IRepository<FaceJob> jobs,
    IRepository<EventPhoto> photos,
    IUnitOfWork uow,
    IFaceAnalyzer? analyzer = null) : IFaceGroupingService
{
    /// <summary>Groups in fewer photos than this aren't offered as a filter unless the host named them: a crowd shot would otherwise add a row per stranger.</summary>
    public const int MinPhotosToShow = 2;

    public bool Available => analyzer?.Available ?? false;

    public static bool IsPicture(EventPhoto p) => p.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    public async Task SetAsync(MediaBucket bucket, bool on, bool guestsTold, Guid? by, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (on)
        {
            if (!Available) throw new InvalidStateException("Face grouping isn't available right now.", "faces_unavailable");
            if (!guestsTold)
                throw new BusinessRuleException("Tick the box to confirm your guests are told photos are grouped by face.", "faces_guests_not_told");
            if (bucket.FaceGroupingOn) return;
            // Start clean: no face or group from an earlier time it was on (or a sweep that hadn't finished)
            // carries over. Jobs still queued are kept and the missing ones added below.
            await ClearAsync(bucket.Id, ct, keepJobs: true);
            bucket.FaceGroupingOn = true;
            bucket.FaceGroupingConfirmedAt = now;
            bucket.FaceGroupingConfirmedBy = by;
            bucket.UpdatedAt = now;
            buckets.Update(bucket);

            var queued = await jobs.Query().Where(j => j.BucketId == bucket.Id).Select(j => j.Id).ToListAsync(ct);
            var pictures = await photos.Query()
                .Where(p => p.BucketId == bucket.Id && p.DeletedAt == null && p.ContentType.StartsWith("image/"))
                .OrderBy(p => p.CreatedAt)
                .Select(p => p.Id).ToListAsync(ct);
            foreach (var id in pictures.Except(queued))
                await jobs.AddAsync(NewJob(id, bucket.Id, now), ct);
            await uow.SaveChangesAsync(ct);
            return;
        }

        bucket.FaceGroupingOn = false;
        bucket.UpdatedAt = now;
        buckets.Update(bucket);
        await ClearAsync(bucket.Id, ct);
        await uow.SaveChangesAsync(ct);
    }

    /// <summary>Everything face-related in an album, gone (added to the unit of work).</summary>
    private async Task ClearAsync(Guid bucketId, CancellationToken ct, bool keepJobs = false)
    {
        faces.RemoveRange(await faces.Query(tracking: true).Where(f => f.BucketId == bucketId).ToListAsync(ct));
        people.RemoveRange(await people.Query(tracking: true).Where(p => p.BucketId == bucketId).ToListAsync(ct));
        if (!keepJobs) jobs.RemoveRange(await jobs.Query(tracking: true).Where(j => j.BucketId == bucketId).ToListAsync(ct));
    }

    private static FaceJob NewJob(Guid photoId, Guid bucketId, DateTimeOffset now) => new()
    {
        Id = photoId,
        BucketId = bucketId,
        Status = FaceJobStatus.Waiting,
        NextAttemptAt = now,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public async Task QueueAsync(EventPhoto photo, CancellationToken ct)
    {
        if (photo.BucketId is not { } bucketId || !IsPicture(photo)) return;
        var on = await buckets.Query().AnyAsync(b => b.Id == bucketId && b.FaceGroupingOn, ct);
        if (!on) return;
        await jobs.AddAsync(NewJob(photo.Id, bucketId, DateTimeOffset.UtcNow), ct);
    }

    public async Task ForgetPhotosAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken ct)
    {
        if (photoIds.Count == 0) return;
        var ids = photoIds.ToList();
        var gone = await faces.Query(tracking: true).Where(f => ids.Contains(f.PhotoId)).ToListAsync(ct);
        var waiting = await jobs.Query(tracking: true).Where(j => ids.Contains(j.Id)).ToListAsync(ct);
        if (gone.Count == 0 && waiting.Count == 0) return;
        faces.RemoveRange(gone);
        jobs.RemoveRange(waiting);
        await uow.SaveChangesAsync(ct);
    }

    public async Task<(FaceGroupingDto? Dto, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> PeopleByPhoto)> ForBoxAsync(
        IReadOnlyCollection<Guid> bucketIds, IReadOnlyList<EventPhoto> shown, bool canName, CancellationToken ct)
    {
        var empty = (IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>)new Dictionary<Guid, IReadOnlyList<Guid>>();
        var ids = bucketIds.Distinct().ToList();
        var onIds = await buckets.Query().Where(b => ids.Contains(b.Id) && b.FaceGroupingOn).Select(b => b.Id).ToListAsync(ct);
        if (onIds.Count == 0) return (null, empty);

        var waiting = await jobs.Query().CountAsync(j => onIds.Contains(j.BucketId) && j.Status != FaceJobStatus.Failed, ct);
        var groups = await people.Query().Where(p => onIds.Contains(p.BucketId) && !p.Ignored)
            .Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        var rows = await faces.Query().Where(f => onIds.Contains(f.BucketId))
            .Select(f => new { f.PhotoId, f.PersonId, f.X, f.Y, f.W, f.H, f.Score }).ToListAsync(ct);

        var byId = shown.ToDictionary(p => p.Id);
        rows = rows.Where(r => byId.ContainsKey(r.PhotoId)).ToList();
        var peopleByPhoto = rows.GroupBy(r => r.PhotoId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(r => r.PersonId).Distinct().ToList());

        var list = new List<FacePersonDto>();
        foreach (var g in groups)
        {
            var mine = rows.Where(r => r.PersonId == g.Id).ToList();
            var count = mine.Select(r => r.PhotoId).Distinct().Count();
            if (count == 0 || (count < MinPhotosToShow && g.Name is null)) continue;
            // The clearest look at them: the biggest face the detector was sure of.
            var cover = mine.OrderByDescending(r => r.W * r.H * r.Score).First();
            list.Add(new FacePersonDto(g.Id, g.Name, count, byId[cover.PhotoId].ThumbUrl, cover.X, cover.Y, cover.W, cover.H));
        }
        list = list.OrderByDescending(p => p.Name is not null).ThenByDescending(p => p.PhotoCount).ToList();
        return (new FaceGroupingDto(true, canName, waiting, list), peopleByPhoto);
    }

    public async Task<FacePersonDto> NameAsync(Guid personId, string? name, IReadOnlyList<EventPhoto> shown, CancellationToken ct)
    {
        var person = await people.Query(tracking: true).FirstOrDefaultAsync(p => p.Id == personId && !p.Ignored, ct)
                     ?? throw new NotFoundException("That group is no longer there.");
        var clean = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (clean is { Length: > FacePerson.MaxNameLength }) clean = clean[..FacePerson.MaxNameLength].TrimEnd();
        person.Name = clean;
        person.UpdatedAt = DateTimeOffset.UtcNow;
        people.Update(person);
        await uow.SaveChangesAsync(ct);
        var (dto, _) = await ForBoxAsync([person.BucketId], shown, true, ct);
        return dto?.People.FirstOrDefault(p => p.Id == personId)
               ?? throw new NotFoundException("That group is no longer there.");
    }

    public Task<bool> OnForBucketAsync(Guid bucketId, CancellationToken ct) =>
        buckets.Query().AnyAsync(b => b.Id == bucketId && b.FaceGroupingOn, ct);

    public Task<bool> OnForCampaignAsync(Guid campaignId, CancellationToken ct) =>
        buckets.Query().AnyAsync(b => b.CampaignId == campaignId && b.FaceGroupingOn, ct);

    public async Task IgnoreAsync(Guid personId, CancellationToken ct)
    {
        var person = await people.Query(tracking: true).FirstOrDefaultAsync(p => p.Id == personId, ct)
                     ?? throw new NotFoundException("That group is no longer there.");
        if (person.Ignored) return;
        // Only the average stays, to keep new photos of them out of grouping; no name, no faces, no boxes.
        person.Ignored = true;
        person.Name = null;
        person.UpdatedAt = DateTimeOffset.UtcNow;
        people.Update(person);
        faces.RemoveRange(await faces.Query(tracking: true).Where(f => f.PersonId == personId).ToListAsync(ct));
        await uow.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Putting a new face in a group: the closest group's average, if close enough, else a new group. Pure, so the
/// rule can be tested with numbers. Fingerprints are unit length, so closeness is a dot product (cosine).
/// </summary>
public static class FaceClustering
{
    /// <summary>
    /// How close to a group's average a face must be to join it. SFace scores the same person ~0.75-0.9 and
    /// relatives up to ~0.4 on the reference photos (docs: OpenCV's own same-person line is 0.363).
    /// </summary>
    public const float DefaultThreshold = 0.45f;

    public static float Similarity(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var s = 0f;
        for (var i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>The closest group at or above the threshold, ignored groups included (the caller drops those faces).</summary>
    public static FacePerson? Match(float[] embedding, IEnumerable<FacePerson> groups, float threshold = DefaultThreshold)
    {
        FacePerson? best = null;
        var bestScore = threshold;
        foreach (var g in groups)
        {
            var s = Similarity(embedding, g.Centroid);
            if (s >= bestScore)
            {
                best = g;
                bestScore = s;
            }
        }
        return best;
    }

    /// <summary>Adds a face to a group's average (kept unit length).</summary>
    public static void Add(FacePerson group, float[] embedding)
    {
        var n = group.FaceCount;
        var c = group.Centroid.Length == embedding.Length ? group.Centroid.ToArray() : new float[embedding.Length];
        for (var i = 0; i < c.Length; i++) c[i] = c[i] * n + embedding[i];
        var norm = MathF.Sqrt(c.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < c.Length; i++) c[i] /= norm;
        group.Centroid = c;
        group.FaceCount = n + 1;
    }
}
