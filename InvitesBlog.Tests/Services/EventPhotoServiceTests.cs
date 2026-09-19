using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.MediaBuckets;
using InvitesBlog.Application.Services.Photos;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.IO.Compression;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The event photo box. Its whole surface is "who may see and who may remove", and both questions
/// are answered for a caller who is usually not signed in at all — so the doors are what these test.
/// </summary>
public class EventPhotoServiceTests
{
    public EventPhotoServiceTests()
    {
        // Every upload resolves a bucket first. A roomy one, so a full-bucket refusal never stands in
        // for the authorization answer a test was actually asking about.
        _buckets.ForCampaignAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new MediaBucket
            {
                Id = Guid.NewGuid(),
                CampaignId = ci.Arg<Guid>(),
                CapacityBytes = long.MaxValue,
            });

        // A guest may see every bucket unless a test shuts one. Bucket membership is
        // MediaBucketServiceTests' subject; here it only has to be answered.
        _buckets.GuestViewAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new GuestBucketView(new HashSet<Guid>(), SeesUnbucketed: true));
    }

    private readonly IRepository<EventPhoto> _photos = Substitute.For<IRepository<EventPhoto>>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly IGuestRepository _guests = Substitute.For<IGuestRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly IInviterRepository _inviters = Substitute.For<IInviterRepository>();
    private readonly IStorageService _storage = Substitute.For<IStorageService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    /// <summary>
    /// Substituted rather than real: these tests are about the box's doors, and a bucket that always
    /// has room keeps the quota out of the way of that. The bucket's own rules are
    /// <see cref="MediaBucketServiceTests"/>' subject.
    /// </summary>
    private readonly IMediaBucketService _buckets = Substitute.For<IMediaBucketService>();

    // The real optimizer: producing the two derivatives IS what upload does, so a stub would stop
    // these noticing if it started handing back empty or unresized bytes.
    private readonly IImageOptimizer _optimizer =
        new InvitesBlog.Infrastructure.Images.ImageSharpOptimizer(
            NullLogger<InvitesBlog.Infrastructure.Images.ImageSharpOptimizer>.Instance);

    private EventPhotoService Sut() => new(
        _photos, _campaigns, _guests,
        new CampaignOwnershipService(_currentUser, _users, _campaigns, _inviters, TestData.NoCelebrants(), TestData.Empty<Venue>(), TestData.Empty<VenueStaff>()),
        _currentUser, _storage, _optimizer, _buckets, _uow);

    /// <summary>
    /// A photo-like image — noise, so the two derivatives can't both compress away to nothing and make
    /// the size comparison below meaningless.
    /// </summary>
    private static byte[] Jpeg(int w = 3000, int h = 2000)
    {
        using var image = new Image<Rgba32>(w, h);
        var rng = new Random(1);
        image.Mutate(ctx => ctx.ProcessPixelRowsAsVector4(row =>
        {
            for (var x = 0; x < row.Length; x++)
                row[x] = new System.Numerics.Vector4(
                    (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), 1f);
        }));

        using var ms = new MemoryStream();
        image.Save(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder());
        return ms.ToArray();
    }

    /// <summary>The same noise as a lossless PNG — the only way to get a genuinely large fixture.</summary>
    private static byte[] Png(int w, int h)
    {
        using var image = new Image<Rgba32>(w, h);
        var rng = new Random(2);
        image.Mutate(ctx => ctx.ProcessPixelRowsAsVector4(row =>
        {
            for (var x = 0; x < row.Length; x++)
                row[x] = new System.Numerics.Vector4(
                    (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), 1f);
        }));

        using var ms = new MemoryStream();
        image.Save(ms, new SixLabors.ImageSharp.Formats.Png.PngEncoder
        {
            CompressionLevel = SixLabors.ImageSharp.Formats.Png.PngCompressionLevel.NoCompression,
        });
        return ms.ToArray();
    }

    /// <summary>A guest of this campaign, wired up so both doors can be opened or shut per test.</summary>
    private (Campaign Campaign, Guest Guest) OnTheGuestList()
    {
        var campaign = TestData.Campaign(status: CampaignStatus.Dispatched);
        var guest = TestData.Guest(campaign.Id);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);
        _photos.Query().Returns(Array.Empty<EventPhoto>().AsAsyncQueryable());
        _storage.PutAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c => $"/assets/{c.ArgAt<string>(0)}");
        return (campaign, guest);
    }

    /// <summary>A stored photo, with its bytes sitting where the archive will look for them.</summary>
    private EventPhoto Stored(Guid campaignId, string? who = null, string ext = ".jpg",
        byte[]? bytes = null, DateTimeOffset? at = null)
    {
        var id = Guid.NewGuid();
        var key = $"campaigns/{campaignId:N}/photos/{id:N}_o{ext}";
        _storage.GetAsync(key, Arg.Any<CancellationToken>()).Returns(bytes ?? [1, 2, 3, 4]);
        return new EventPhoto
        {
            Id = id, CampaignId = campaignId, UploaderName = who,
            OriginalUrl = $"/assets/{key}", Url = $"/assets/{key}", ThumbUrl = $"/assets/{key}",
            ContentType = "image/jpeg", SizeBytes = 4, Width = 10, Height = 10,
            CreatedAt = at ?? DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Runs the archive into memory and hands back the zip plus the offered filename.</summary>
    private async Task<(string Name, ZipArchive Zip, MemoryStream Raw)> ArchiveAsync(
        Guid campaignId, Guid? viewer, IReadOnlyCollection<Guid>? ids = null)
    {
        var buffer = new MemoryStream();
        var offered = string.Empty;
        await Sut().WriteArchiveAsync(campaignId, viewer, ids, name => { offered = name; return buffer; });
        buffer.Position = 0;
        return (offered, new ZipArchive(buffer, ZipArchiveMode.Read), buffer);
    }

    // ----- buckets a guest is shut out of --------------------------------------------------------

    /// <summary>A guest who may see <paramref name="open"/> and nothing else, unbucketed photos included.</summary>
    private void OnlySees(params Guid[] open) =>
        _buckets.GuestViewAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new GuestBucketView(open.ToHashSet(), SeesUnbucketed: false));

    private EventPhoto InBucket(Guid campaignId, Guid? bucketId)
    {
        var photo = Stored(campaignId);
        photo.BucketId = bucketId;
        return photo;
    }

    /// <summary>
    /// The organiser switched this guest off for the after-party. The bucket's own door already said
    /// no; the campaign's box must not hand the same photographs over anyway.
    /// </summary>
    [Fact]
    public async Task A_guest_shut_out_of_a_bucket_sees_none_of_its_photos_but_still_sees_the_rest()
    {
        var (campaign, guest) = OnTheGuestList();
        Guid ceremony = Guid.NewGuid(), afterParty = Guid.NewGuid();
        EventPhoto[] taken =
        [
            InBucket(campaign.Id, ceremony),
            InBucket(campaign.Id, afterParty),
            InBucket(campaign.Id, afterParty),
            // Predates buckets, so it belongs to the default bucket's audience — shut here too.
            InBucket(campaign.Id, null),
        ];
        _photos.Query().Returns(taken.AsAsyncQueryable());
        OnlySees(ceremony);

        var box = await Sut().GetAsync(campaign.Id, guest.Id);

        Assert.Single(box.Photos);
        Assert.Equal(taken[0].Id, box.Photos[0].Id);
        Assert.Equal(1, box.Count);
    }

    [Fact]
    public async Task A_guest_allowed_into_a_bucket_sees_its_photos()
    {
        var (campaign, guest) = OnTheGuestList();
        var afterParty = Guid.NewGuid();
        EventPhoto[] taken = [InBucket(campaign.Id, afterParty), InBucket(campaign.Id, afterParty)];
        _photos.Query().Returns(taken.AsAsyncQueryable());
        OnlySees(afterParty);

        var box = await Sut().GetAsync(campaign.Id, guest.Id);

        Assert.Equal(2, box.Photos.Count);
    }

    /// <summary>Keeping is the same question as looking: what cannot be seen cannot be downloaded.</summary>
    [Fact]
    public async Task A_guest_shut_out_of_a_bucket_cannot_download_its_photos()
    {
        var (campaign, guest) = OnTheGuestList();
        Guid ceremony = Guid.NewGuid(), afterParty = Guid.NewGuid();
        EventPhoto[] taken = [InBucket(campaign.Id, ceremony), InBucket(campaign.Id, afterParty)];
        _photos.Query().Returns(taken.AsAsyncQueryable());
        OnlySees(ceremony);

        var (_, zip, _) = await ArchiveAsync(campaign.Id, guest.Id);
        Assert.Single(zip.Entries);

        // And naming the hidden one outright gets nothing, rather than an empty zip or the photo.
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => ArchiveAsync(campaign.Id, guest.Id, new[] { taken[1].Id }));
    }

    /// <summary>Restricting a bucket narrows what GUESTS see. The host moderates all of it.</summary>
    [Fact]
    public async Task The_host_still_sees_every_bucket()
    {
        var (campaign, _) = OnTheGuestList();
        _currentUser.HasPermission(Permissions.Photos.Moderate).Returns(true);
        _currentUser.CampaignId.Returns(campaign.Id);
        Guid ceremony = Guid.NewGuid(), afterParty = Guid.NewGuid();
        EventPhoto[] taken = [InBucket(campaign.Id, ceremony), InBucket(campaign.Id, afterParty), InBucket(campaign.Id, null)];
        _photos.Query().Returns(taken.AsAsyncQueryable());
        OnlySees(ceremony);

        var box = await Sut().GetAsync(campaign.Id, viewerGuestId: null);

        Assert.Equal(3, box.Photos.Count);
        await _buckets.DidNotReceiveWithAnyArgs().GuestViewAsync(default, default, default);
    }

    // ----- keeping the photographs -------------------------------------------------------------

    /// <summary>
    /// What the response body actually is: write-only, unseekable, and refusing every synchronous
    /// write — Kestrel with buffering off. The archive used to be written with the synchronous
    /// ZipArchive, which passed against a MemoryStream and failed every real download.
    /// </summary>
    private sealed class ResponseBodyLike : Stream
    {
        private readonly MemoryStream _inner = new();
        public byte[] ToArray() => _inner.ToArray();

        private static InvalidOperationException Sync() =>
            new("Synchronous operations are disallowed. Call WriteAsync or set AllowSynchronousIO to true instead.");

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Flush() => throw Sync();
        public override void Write(byte[] buffer, int offset, int count) => throw Sync();
        public override void Write(ReadOnlySpan<byte> buffer) => throw Sync();
        public override void WriteByte(byte value) => throw Sync();

        public override Task FlushAsync(CancellationToken ct) => _inner.FlushAsync(ct);
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            _inner.WriteAsync(buffer, offset, count, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) =>
            _inner.WriteAsync(buffer, ct);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Everything uploaded since buckets exist is stored under buckets/, not campaigns/. The archive
    /// only recognised the old root, so every download of a current event was an empty zip.
    /// </summary>
    [Fact]
    public async Task Photos_stored_in_a_bucket_are_packed()
    {
        var (campaign, guest) = OnTheGuestList();
        var bucketId = Guid.NewGuid();
        var id = Guid.NewGuid();
        var key = $"buckets/{bucketId:N}/media/{id:N}_o.jpg";
        _storage.GetAsync(key, Arg.Any<CancellationToken>()).Returns([9, 9, 9]);
        EventPhoto[] taken =
        [
            new()
            {
                Id = id, CampaignId = campaign.Id, BucketId = bucketId,
                OriginalUrl = $"http://localhost:8080/assets/{key}", Url = $"/assets/buckets/{bucketId:N}/media/{id:N}.jpg",
                ThumbUrl = $"/assets/buckets/{bucketId:N}/media/{id:N}_t.jpg",
                ContentType = "image/jpeg", CreatedAt = DateTimeOffset.UtcNow,
            },
            Stored(campaign.Id),
        ];
        _photos.Query().Returns(taken.AsAsyncQueryable());
        _buckets.GuestViewAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new GuestBucketView(new HashSet<Guid> { bucketId }, SeesUnbucketed: true));

        var (_, zip, _) = await ArchiveAsync(campaign.Id, guest.Id);

        Assert.Equal(2, zip.Entries.Count);
    }

    [Fact]
    public async Task The_archive_streams_into_a_body_that_refuses_synchronous_writes()
    {
        var (campaign, guest) = OnTheGuestList();
        EventPhoto[] taken = [Stored(campaign.Id, bytes: [1, 2, 3]), Stored(campaign.Id, bytes: [4, 5, 6, 7])];
        _photos.Query().Returns(taken.AsAsyncQueryable());
        var body = new ResponseBodyLike();

        await Sut().WriteArchiveAsync(campaign.Id, guest.Id, null, _ => body);

        using var zip = new ZipArchive(new MemoryStream(body.ToArray()), ZipArchiveMode.Read);
        Assert.Equal(2, zip.Entries.Count);
        await using var first = await zip.Entries.OrderBy(e => e.FullName).First().OpenAsync();
        using var read = new MemoryStream();
        await first.CopyToAsync(read);
        Assert.Equal(new byte[] { 1, 2, 3 }, read.ToArray());
    }

    [Fact]
    public async Task A_guest_can_download_the_whole_box()
    {
        var (campaign, guest) = OnTheGuestList();
        // Seeded before the query is stubbed: configuring one substitute inside another's Returns()
        // is the one thing NSubstitute cannot see through.
        EventPhoto[] taken = [Stored(campaign.Id), Stored(campaign.Id)];
        _photos.Query().Returns(taken.AsAsyncQueryable());

        var (name, zip, _) = await ArchiveAsync(campaign.Id, guest.Id);

        Assert.Equal(2, zip.Entries.Count);
        Assert.EndsWith("-photos.zip", name);
    }

    /// <summary>Selecting some of them is the point of the checkboxes.</summary>
    [Fact]
    public async Task Only_the_named_photos_are_packed()
    {
        var (campaign, guest) = OnTheGuestList();
        var wanted = Stored(campaign.Id);
        EventPhoto[] taken = [wanted, Stored(campaign.Id), Stored(campaign.Id)];
        _photos.Query().Returns(taken.AsAsyncQueryable());

        var (_, zip, _) = await ArchiveAsync(campaign.Id, guest.Id, new[] { wanted.Id });

        Assert.Single(zip.Entries);
    }

    /// <summary>The same door that guards looking. A stranger cannot keep what they cannot see.</summary>
    [Fact]
    public async Task A_guest_of_a_different_event_cannot_download_this_one()
    {
        var (campaign, _) = OnTheGuestList();
        var elsewhere = TestData.Guest(Guid.NewGuid());
        EventPhoto[] taken = [Stored(campaign.Id)];
        _guests.GetByIdAsync(elsewhere.Id, Arg.Any<CancellationToken>()).Returns(elsewhere);
        _photos.Query().Returns(taken.AsAsyncQueryable());

        await Assert.ThrowsAsync<ForbiddenException>(
            () => ArchiveAsync(campaign.Id, elsewhere.Id));
    }

    /// <summary>
    /// Nothing may be written before the caller is known to be allowed — the response commits its
    /// status the moment the body starts, so a refusal that arrived late would read as a corrupt file.
    /// </summary>
    [Fact]
    public async Task A_refusal_never_opens_the_response()
    {
        var (campaign, _) = OnTheGuestList();
        var elsewhere = TestData.Guest(Guid.NewGuid());
        EventPhoto[] taken = [Stored(campaign.Id)];
        _guests.GetByIdAsync(elsewhere.Id, Arg.Any<CancellationToken>()).Returns(elsewhere);
        _photos.Query().Returns(taken.AsAsyncQueryable());

        var opened = false;
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().WriteArchiveAsync(
            campaign.Id, elsewhere.Id, null, _ => { opened = true; return new MemoryStream(); }));

        Assert.False(opened);
    }

    [Fact]
    public async Task An_empty_box_is_refused_rather_than_sent_as_an_empty_zip()
    {
        var (campaign, guest) = OnTheGuestList();
        _photos.Query().Returns(Array.Empty<EventPhoto>().AsAsyncQueryable());

        await Assert.ThrowsAsync<BusinessRuleException>(() => ArchiveAsync(campaign.Id, guest.Id));
    }

    /// <summary>
    /// A photo whose object has gone from storage must cost only itself. The rest of somebody's
    /// evening still comes down.
    /// </summary>
    [Fact]
    public async Task A_missing_object_does_not_lose_the_rest_of_the_archive()
    {
        var (campaign, guest) = OnTheGuestList();
        var gone = Stored(campaign.Id);
        EventPhoto[] taken = [gone, Stored(campaign.Id)];
        _storage.GetAsync(Arg.Is<string>(k => k.Contains(gone.Id.ToString("N"))), Arg.Any<CancellationToken>())
            .Returns((byte[]?)null);
        _photos.Query().Returns(taken.AsAsyncQueryable());

        var (_, zip, _) = await ArchiveAsync(campaign.Id, guest.Id);

        Assert.Single(zip.Entries);
    }

    /// <summary>
    /// Two guests with the same name would otherwise write the same entry twice, and some readers
    /// unpack that to one file.
    /// </summary>
    [Fact]
    public async Task Entries_are_numbered_credited_and_never_collide()
    {
        var (campaign, guest) = OnTheGuestList();
        var t = DateTimeOffset.UtcNow;
        EventPhoto[] taken = [
            Stored(campaign.Id, who: "Ali", at: t),
            Stored(campaign.Id, who: "Ali", at: t.AddMinutes(1)),
        ];
        _photos.Query().Returns(taken.AsAsyncQueryable());

        var (_, zip, _) = await ArchiveAsync(campaign.Id, guest.Id);

        var names = zip.Entries.Select(e => e.FullName).OrderBy(n => n).ToList();
        Assert.Equal(2, names.Distinct().Count());
        Assert.All(names, n => Assert.Contains("Ali", n));
        Assert.Contains(names, n => n.StartsWith("001"));
    }

    /// <summary>A guest names their file whatever their phone named it; a zip entry name is a path.</summary>
    [Fact]
    public async Task A_hostile_uploader_name_cannot_escape_the_archive()
    {
        var (campaign, guest) = OnTheGuestList();
        EventPhoto[] taken = [Stored(campaign.Id, who: "../../etc/passwd")];
        _photos.Query().Returns(taken.AsAsyncQueryable());

        var (_, zip, _) = await ArchiveAsync(campaign.Id, guest.Id);

        var entry = Assert.Single(zip.Entries).FullName;
        Assert.DoesNotContain("..", entry);
        Assert.DoesNotContain("/", entry);
    }

    // ----- who may look -------------------------------------------------------------------------

    [Fact]
    public async Task A_guest_of_the_event_sees_its_photo_box()
    {
        var (campaign, guest) = OnTheGuestList();

        var box = await Sut().GetAsync(campaign.Id, guest.Id);

        Assert.Equal(campaign.Id, box.CampaignId);
    }

    /// <summary>
    /// The guest id is supplied by the caller, so it is the one input that must never be taken at face
    /// value: a guest authenticated for one event would otherwise read every other event's photos by
    /// passing its campaign id alongside their own guest id.
    /// </summary>
    [Fact]
    public async Task A_guest_of_a_DIFFERENT_event_is_refused()
    {
        var (campaign, _) = OnTheGuestList();
        var elsewhere = TestData.Guest(Guid.NewGuid());
        _guests.GetByIdAsync(elsewhere.Id, Arg.Any<CancellationToken>()).Returns(elsewhere);

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().GetAsync(campaign.Id, elsewhere.Id));
    }

    [Fact]
    public async Task A_stranger_with_no_guest_row_and_no_ownership_is_refused()
    {
        var (campaign, _) = OnTheGuestList();

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().GetAsync(campaign.Id, null));
    }

    /// <summary>
    /// Every customer account holds photos.moderate — it says "may moderate a photo box", never "may
    /// moderate THIS one". Ownership is the half that scopes it, and without it the permission alone
    /// would open every event on the platform.
    /// </summary>
    [Fact]
    public async Task The_moderation_permission_alone_does_not_open_someone_elses_event()
    {
        var (campaign, _) = OnTheGuestList();
        _currentUser.HasPermission(Permissions.Photos.Moderate).Returns(true);
        _currentUser.UserId.Returns(Guid.NewGuid());
        _currentUser.CampaignId.Returns((Guid?)null);
        _users.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((AppUser?)null);

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().GetAsync(campaign.Id, null));
    }

    [Fact]
    public async Task The_host_holding_the_campaign_token_sees_the_box()
    {
        var (campaign, _) = OnTheGuestList();
        _currentUser.HasPermission(Permissions.Photos.Moderate).Returns(true);
        _currentUser.CampaignId.Returns(campaign.Id);   // the possession-token host

        var box = await Sut().GetAsync(campaign.Id, null);

        Assert.Equal(campaign.Id, box.CampaignId);
    }

    // ----- whether the box says it is open ------------------------------------------------------

    /// <summary>
    /// The box reported only cancellation, so an event six months past still offered "Add media" —
    /// and pressing it produced the refusal <see cref="StoreAsync"/> was always going to give. The
    /// reader has to answer the same question the writer does, or the button is a lie.
    /// </summary>
    [Fact]
    public async Task An_event_that_is_over_offers_nothing_to_add_and_says_why()
    {
        var (campaign, guest) = OnTheGuestList();
        campaign.EventStartAt = DateTimeOffset.UtcNow.AddDays(-30);

        var box = await Sut().GetAsync(campaign.Id, guest.Id);

        Assert.False(box.CanUpload);
        Assert.Contains("closed", box.ClosedNote);
    }

    /// <summary>
    /// The other side of the window. "Closed" means two completely different things to somebody
    /// standing at the party a day early and somebody looking a week later.
    /// </summary>
    [Fact]
    public async Task An_event_still_to_come_says_it_opens_on_the_day()
    {
        var (campaign, guest) = OnTheGuestList();
        campaign.EventStartAt = DateTimeOffset.UtcNow.AddDays(30);

        var box = await Sut().GetAsync(campaign.Id, guest.Id);

        Assert.False(box.CanUpload);
        Assert.Contains("opens on the day", box.ClosedNote);
    }

    [Fact]
    public async Task On_the_night_the_box_is_open_and_says_nothing()
    {
        var (campaign, guest) = OnTheGuestList();
        campaign.EventStartAt = DateTimeOffset.UtcNow.AddHours(-1);

        var box = await Sut().GetAsync(campaign.Id, guest.Id);

        Assert.True(box.CanUpload);
        Assert.Null(box.ClosedNote);
    }

    /// <summary>A cancelled event is a read-only archive whatever the calendar says.</summary>
    [Fact]
    public async Task A_cancelled_event_is_closed_even_on_its_own_night()
    {
        var (campaign, guest) = OnTheGuestList();
        campaign.EventStartAt = DateTimeOffset.UtcNow.AddHours(-1);
        campaign.Status = CampaignStatus.Cancelled;

        var box = await Sut().GetAsync(campaign.Id, guest.Id);

        Assert.False(box.CanUpload);
        Assert.Contains("cancelled", box.ClosedNote);
    }

    // ----- adding -------------------------------------------------------------------------------

    [Fact]
    public async Task A_guest_photo_is_stored_at_two_sizes_and_credited_to_them()
    {
        var (campaign, guest) = OnTheGuestList();

        var photo = await Sut().AddAsync(campaign.Id, guest.Id, Jpeg(), "image/jpeg", "IMG_0042.jpg");

        // Three objects: the shot as taken, the one a tap opens, and the one the grid loads.
        await _storage.Received(3).PutAsync(
            Arg.Any<string>(), Arg.Any<byte[]>(), "image/jpeg", Arg.Any<CancellationToken>());
        Assert.NotEqual(photo.Url, photo.ThumbUrl);
        Assert.NotEqual(photo.OriginalUrl, photo.Url);
        Assert.Equal(guest.Name, photo.UploaderName);
        Assert.True(photo.CanDelete);
    }

    /// <summary>
    /// The reason there are two objects at all. A box is the one screen that renders hundreds of
    /// images at once, on a phone, at an event — if the grid ends up loading the viewing copy the
    /// derivative has bought nothing.
    /// </summary>
    [Fact]
    public async Task The_viewing_copy_is_bigger_than_the_grid_copy()
    {
        var (campaign, guest) = OnTheGuestList();
        var stored = new List<(string Key, byte[] Bytes)>();
        _storage.PutAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                stored.Add((c.ArgAt<string>(0), c.ArgAt<byte[]>(1)));
                return $"/assets/{c.ArgAt<string>(0)}";
            });

        await Sut().AddAsync(campaign.Id, guest.Id, Jpeg(), "image/jpeg", "IMG_0042.jpg");

        var original = stored.Single(s => s.Key.Contains("_o", StringComparison.Ordinal));
        var view = stored.Single(s => !s.Key.Contains("_t", StringComparison.Ordinal)
                                      && !s.Key.Contains("_o", StringComparison.Ordinal));
        var thumb = stored.Single(s => s.Key.Contains("_t", StringComparison.Ordinal));

        Assert.True(view.Bytes.Length > thumb.Bytes.Length,
            "the grid copy must be smaller than the one a tap opens, or the box crawls on a phone");
        Assert.True(original.Bytes.Length > view.Bytes.Length,
            "the kept copy must be bigger than the viewing copy, or nothing was preserved");
    }

    [Fact]
    public async Task A_cancelled_event_stops_accepting_photos()
    {
        var (campaign, guest) = OnTheGuestList();
        campaign.Status = CampaignStatus.Cancelled;

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().AddAsync(campaign.Id, guest.Id, Jpeg(), "image/jpeg", "x.jpg"));

        Assert.Equal("campaign_cancelled", ex.ErrorCode);
    }

    /// <summary>
    /// Size limits belong on the images a TEMPLATE renders, not on somebody's photograph of the night.
    /// The framework ceilings are lifted per-endpoint for exactly this; nothing in the service may put
    /// one back.
    /// </summary>
    [Fact]
    public async Task A_large_photo_is_not_refused_for_being_large()
    {
        var (campaign, guest) = OnTheGuestList();

        // A lossless PNG of noise, because JPEG compresses this fixture far below the size being
        // tested. Comfortably past both the 24 MB multipart cap that still governs template images
        // and the 25 MB per-file cap this box used to carry.
        var big = Png(3000, 3000);
        Assert.True(big.Length > 25 * 1024 * 1024, $"fixture must exceed the old cap; was {big.Length} bytes");

        var photo = await Sut().AddAsync(campaign.Id, guest.Id, big, "image/png", "IMG_0100.png");

        // And it is kept at full size: the original is the shot, not a copy sized for a screen.
        Assert.Equal(3000, photo.Width);
        Assert.Equal(3000, photo.Height);
    }

    private const string Svg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" onload=\"alert(1)\"><script>alert(document.cookie)</script></svg>";

    /// <summary>
    /// Everything uploaded here is served back on the app's own origin. An SVG there is a document
    /// that runs script with the session in reach, not a photograph.
    /// </summary>
    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("image/png")]      // the label lies: SVG bytes under a raster type
    [InlineData("image/jpeg")]
    public async Task An_svg_is_refused_whatever_it_is_labelled(string contentType)
    {
        var (campaign, guest) = OnTheGuestList();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sut().AddAsync(
            campaign.Id, guest.Id, System.Text.Encoding.UTF8.GetBytes(Svg), contentType, "x.png"));

        Assert.Equal("unsupported_image", ex.ErrorCode);
        await _storage.DidNotReceiveWithAnyArgs().PutAsync(default!, default!, default!, default);
    }

    /// <summary>And a real photograph labelled SVG is refused too — the label is what it is served as.</summary>
    [Fact]
    public async Task A_photo_labelled_as_svg_is_refused()
    {
        var (campaign, guest) = OnTheGuestList();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().AddAsync(campaign.Id, guest.Id, Jpeg(64, 64), "image/svg+xml", "x.svg"));

        Assert.Equal("unsupported_image", ex.ErrorCode);
    }

    /// <summary>The QR contributor's door has no identity to check, so the file check is all there is.</summary>
    [Fact]
    public async Task A_contributor_cannot_upload_an_svg_into_a_bucket()
    {
        OnTheGuestList();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sut().AddToBucketAsync(
            Guid.NewGuid(), null, "Somebody", System.Text.Encoding.UTF8.GetBytes(Svg), "image/png", "x.png"));

        Assert.Equal("unsupported_image", ex.ErrorCode);
    }

    [Fact]
    public async Task A_clip_whose_still_is_not_a_picture_is_refused()
    {
        var (campaign, guest) = OnTheGuestList();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sut().AddAsync(
            campaign.Id, guest.Id, [.. Enumerable.Repeat((byte)7, 4096)], "video/mp4", "clip.mp4",
            System.Text.Encoding.UTF8.GetBytes(Svg)));

        Assert.Equal("unsupported_image", ex.ErrorCode);
    }

    // ----- the space an upload takes ------------------------------------------------------------

    private MediaBucket OneBucket(Guid campaignId)
    {
        var bucket = new MediaBucket { Id = Guid.NewGuid(), CampaignId = campaignId, CapacityBytes = long.MaxValue };
        _buckets.ForCampaignAsync(campaignId, Arg.Any<CancellationToken>()).Returns(bucket);
        return bucket;
    }

    /// <summary>
    /// Space is claimed BEFORE anything is written, and settled to what was really stored after — so
    /// parallel uploads cannot all pass a check on the same figure, and the count still ends exact.
    /// </summary>
    [Fact]
    public async Task An_upload_reserves_its_space_first_and_settles_to_what_was_stored()
    {
        var (campaign, guest) = OnTheGuestList();
        var bucket = OneBucket(campaign.Id);
        var content = Jpeg(640, 480);
        EventPhoto? recorded = null;
        await _photos.AddAsync(Arg.Do<EventPhoto>(p => recorded = p), Arg.Any<CancellationToken>());

        await Sut().AddAsync(campaign.Id, guest.Id, content, "image/jpeg", "IMG_1.jpg");

        await _buckets.Received(1).ReserveRoomAsync(bucket.Id, content.Length, Arg.Any<CancellationToken>());
        await _buckets.DidNotReceiveWithAnyArgs().EnsureRoomAsync(default, default, default);
        Assert.NotNull(recorded);
        await _buckets.Received(1).CountUsageAsync(
            bucket.Id, recorded!.SizeBytes - content.Length, Arg.Any<CancellationToken>());
    }

    /// <summary>An upload that never made it gives its reservation back, or the bucket fills with nothing.</summary>
    [Fact]
    public async Task A_failed_upload_gives_back_the_space_it_reserved()
    {
        var (campaign, guest) = OnTheGuestList();
        var bucket = OneBucket(campaign.Id);
        var content = Jpeg(64, 64);
        _storage.PutAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<string>>(_ => throw new IOException("storage is down"));

        await Assert.ThrowsAsync<IOException>(
            () => Sut().AddAsync(campaign.Id, guest.Id, content, "image/jpeg", "IMG_1.jpg"));

        await _buckets.Received(1).CountUsageAsync(bucket.Id, -content.Length, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Something_that_is_neither_a_photo_nor_a_video_is_refused()
    {
        var (campaign, guest) = OnTheGuestList();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().AddAsync(campaign.Id, guest.Id, [1, 2, 3], "application/pdf", "cv.pdf"));

        Assert.Equal("not_media", ex.ErrorCode);
    }

    // ----- clips ---------------------------------------------------------------------------------

    /// <summary>
    /// A clip is stored ONCE and pointed at twice. There is no smaller viewing copy to make without
    /// a transcoder, so writing the same file under a second key would double what an evening's
    /// videos cost and buy nothing at all.
    /// </summary>
    [Fact]
    public async Task A_clip_is_stored_once_with_its_poster_as_the_tile()
    {
        var (campaign, guest) = OnTheGuestList();

        var clip = await Sut().AddAsync(
            campaign.Id, guest.Id, [.. Enumerable.Repeat((byte)7, 4096)], "video/mp4", "clip.mp4",
            Jpeg(1280, 720));

        // Two objects, not three: the clip itself, and the still that stands in for it.
        await _storage.Received(2).PutAsync(
            Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.Equal(clip.Url, clip.OriginalUrl);
        Assert.NotEqual(clip.Url, clip.ThumbUrl);
        Assert.Equal("video/mp4", clip.ContentType);
        // The poster was drawn FROM the clip, so its shape is the clip's shape.
        Assert.Equal(1280, clip.Width);
        Assert.Equal(720, clip.Height);
    }

    /// <summary>
    /// The one thing the server cannot recover for itself. It has no decoder, so a clip that arrives
    /// without a still would be a permanently black tile — better to refuse it loudly.
    /// </summary>
    [Fact]
    public async Task A_clip_without_a_poster_is_refused()
    {
        var (campaign, guest) = OnTheGuestList();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().AddAsync(campaign.Id, guest.Id, [1, 2, 3], "video/mp4", "clip.mp4"));

        Assert.Equal("video_without_poster", ex.ErrorCode);
    }

    /// <summary>Photographs stay uncapped; the cap exists because a clip is buffered whole in memory.</summary>
    [Fact]
    public async Task A_clip_past_the_ceiling_is_refused_where_a_photograph_would_not_be()
    {
        var (campaign, guest) = OnTheGuestList();
        var huge = new byte[(256L * 1024 * 1024) + 1];

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().AddAsync(campaign.Id, guest.Id, huge, "video/mp4", "clip.mp4", Jpeg(64, 64)));

        Assert.Equal("video_too_large", ex.ErrorCode);
    }

    /// <summary>A poster sent alongside a photograph is not a second photograph. It is ignored.</summary>
    [Fact]
    public async Task A_poster_offered_with_a_photograph_changes_nothing()
    {
        var (campaign, guest) = OnTheGuestList();

        var photo = await Sut().AddAsync(
            campaign.Id, guest.Id, Jpeg(), "image/jpeg", "IMG_0042.jpg", Jpeg(64, 64));

        await _storage.Received(3).PutAsync(
            Arg.Any<string>(), Arg.Any<byte[]>(), "image/jpeg", Arg.Any<CancellationToken>());
        Assert.NotEqual(photo.Url, photo.OriginalUrl);
        Assert.Equal("image/jpeg", photo.ContentType);
    }

    [Fact]
    public async Task Someone_not_on_the_guest_list_cannot_add_to_the_box()
    {
        var (campaign, _) = OnTheGuestList();
        var elsewhere = TestData.Guest(Guid.NewGuid());
        _guests.GetByIdAsync(elsewhere.Id, Arg.Any<CancellationToken>()).Returns(elsewhere);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Sut().AddAsync(campaign.Id, elsewhere.Id, Jpeg(), "image/jpeg", "x.jpg"));
    }

    // ----- removing -----------------------------------------------------------------------------

    private EventPhoto Existing(Guid campaignId, Guid? takenBy)
    {
        var photo = new EventPhoto
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, GuestId = takenBy,
            Url = "/assets/a.jpg", ThumbUrl = "/assets/a_t.jpg", ContentType = "image/jpeg",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _photos.Query(true).Returns(new[] { photo }.AsAsyncQueryable());
        return photo;
    }

    [Fact]
    public async Task A_guest_may_remove_the_photo_they_took()
    {
        var (campaign, guest) = OnTheGuestList();
        var photo = Existing(campaign.Id, guest.Id);

        await Sut().DeleteAsync(campaign.Id, photo.Id, guest.Id);

        Assert.NotNull(photo.DeletedAt);
    }

    [Fact]
    public async Task A_guest_may_not_remove_somebody_elses_photo()
    {
        var (campaign, guest) = OnTheGuestList();
        var photo = Existing(campaign.Id, Guid.NewGuid());

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().DeleteAsync(campaign.Id, photo.Id, guest.Id));
        Assert.Null(photo.DeletedAt);
    }

    [Fact]
    public async Task The_host_may_remove_any_photo()
    {
        var (campaign, _) = OnTheGuestList();
        var photo = Existing(campaign.Id, Guid.NewGuid());
        _currentUser.HasPermission(Permissions.Photos.Moderate).Returns(true);
        _currentUser.CampaignId.Returns(campaign.Id);

        await Sut().DeleteAsync(campaign.Id, photo.Id, null);

        Assert.NotNull(photo.DeletedAt);
    }

    /// <summary>A double-tap on a phone at a party should not be an error page.</summary>
    [Fact]
    public async Task Removing_a_photo_twice_is_not_an_error()
    {
        var (campaign, guest) = OnTheGuestList();
        var photo = Existing(campaign.Id, guest.Id);
        photo.DeletedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        var first = photo.DeletedAt;

        await Sut().DeleteAsync(campaign.Id, photo.Id, guest.Id);

        Assert.Equal(first, photo.DeletedAt);
    }
}
