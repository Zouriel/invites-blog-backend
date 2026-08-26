using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.Photos;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
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
    private readonly IRepository<EventPhoto> _photos = Substitute.For<IRepository<EventPhoto>>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly IGuestRepository _guests = Substitute.For<IGuestRepository>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly IInviterRepository _inviters = Substitute.For<IInviterRepository>();
    private readonly IStorageService _storage = Substitute.For<IStorageService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    // The real optimizer: producing the two derivatives IS what upload does, so a stub would stop
    // these noticing if it started handing back empty or unresized bytes.
    private readonly IImageOptimizer _optimizer =
        new InvitesBlog.Infrastructure.Images.ImageSharpOptimizer(
            NullLogger<InvitesBlog.Infrastructure.Images.ImageSharpOptimizer>.Instance);

    private EventPhotoService Sut() => new(
        _photos, _campaigns, _guests,
        new CampaignOwnershipService(_currentUser, _users, _campaigns, _inviters),
        _currentUser, _storage, _optimizer, _uow);

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

    [Fact]
    public async Task Something_that_is_not_an_image_is_refused()
    {
        var (campaign, guest) = OnTheGuestList();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().AddAsync(campaign.Id, guest.Id, [1, 2, 3], "application/pdf", "cv.pdf"));

        Assert.Equal("not_an_image", ex.ErrorCode);
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
