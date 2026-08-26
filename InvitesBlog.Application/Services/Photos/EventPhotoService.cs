using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Photos;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Photos;

/// <summary>The event photo box (§5): what guests shot at the party, for everyone who was there.</summary>
public interface IEventPhotoService
{
    /// <summary>
    /// The box as <paramref name="viewerGuestId"/> sees it. Pass the guest whose invitation is open
    /// when the caller is a guest, so their own photos come back deletable; pass null for a host,
    /// whose right to delete comes from moderation instead.
    /// </summary>
    Task<EventPhotoBoxDto> GetAsync(Guid campaignId, Guid? viewerGuestId, CancellationToken ct = default);

    /// <summary>
    /// Adds one photo. <paramref name="guestId"/> is the guest who took it — null when the host adds
    /// one themselves.
    /// </summary>
    Task<EventPhotoDto> AddAsync(
        Guid campaignId, Guid? guestId, byte[] content, string contentType, string fileName,
        CancellationToken ct = default);

    /// <summary>
    /// Removes a photo. Allowed for the guest who took it (<paramref name="actingGuestId"/>) or for a
    /// host holding <see cref="Permissions.Photos.Moderate"/> over the campaign.
    /// </summary>
    Task DeleteAsync(Guid campaignId, Guid photoId, Guid? actingGuestId, CancellationToken ct = default);
}

/// <inheritdoc cref="IEventPhotoService"/>
public sealed class EventPhotoService(
    IRepository<EventPhoto> photos,
    ICampaignRepository campaigns,
    IGuestRepository guestRepository,
    ICampaignOwnershipService ownership,
    ICurrentUser currentUser,
    IStorageService storage,
    IImageOptimizer imageOptimizer,
    IUnitOfWork uow) : IEventPhotoService
{
    /// <summary>
    /// What a tap opens. NOT what a download hands over — the original is kept for that, so this is
    /// free to be a screen-sized copy rather than a compromise between viewing and keeping.
    /// </summary>
    private const int ViewEdge = 2048;

    /// <summary>
    /// Grid size. The box is the one screen guaranteed to render hundreds of images at once, on a
    /// phone, at an event — serving the viewing size into a 120px tile is what makes it crawl.
    /// </summary>
    private const int ThumbEdge = 400;

    public async Task<EventPhotoBoxDto> GetAsync(Guid campaignId, Guid? viewerGuestId, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");

        var moderates = await MayHostAsync(campaignId, ct);
        if (!moderates && !await IsGuestOfAsync(campaignId, viewerGuestId, ct))
            throw new ForbiddenException("This photo box belongs to an event you're not on.");

        var live = await photos.Query()
            .Where(p => p.CampaignId == campaignId && p.DeletedAt == null)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        return new EventPhotoBoxDto(
            campaign.Id,
            campaign.Title,
            live.Count,
            campaign.Status != CampaignStatus.Cancelled,
            live.Select(p => new EventPhotoDto(
                p.Id, p.Url, p.ThumbUrl, p.OriginalUrl, p.Width, p.Height, p.UploaderName,
                moderates || (viewerGuestId is { } me && p.GuestId == me),
                p.CreatedAt)).ToList());
    }

    public async Task<EventPhotoDto> AddAsync(
        Guid campaignId, Guid? guestId, byte[] content, string contentType, string fileName,
        CancellationToken ct = default)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");

        // Who is adding this decides which door they came through: a guest must be ON the guest list,
        // and anyone without a guest row is claiming to be the host and has to prove it.
        if (guestId is null
            ? !await MayHostAsync(campaignId, ct)
            : !await IsGuestOfAsync(campaignId, guestId, ct))
            throw new ForbiddenException("You can only add photos to an event you were invited to.");

        // A cancelled event's box is a read-only archive. Whatever was already shot stays; nothing new
        // arrives, so a cancelled party cannot keep accruing storage against the host's name.
        if (campaign.Status == CampaignStatus.Cancelled)
            throw new BusinessRuleException("This event has been cancelled.", "campaign_cancelled");

        if (content.Length == 0)
            throw new BusinessRuleException("That photo file is empty.", "empty_image");
        if (string.IsNullOrWhiteSpace(contentType)
            || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException("Only photos can be uploaded here.", "not_an_image");

        // NO SIZE CAP, deliberately. Size limits belong on the images a TEMPLATE renders, where a
        // huge upload buys nothing a browser can show and costs every guest the download. This is the
        // opposite case: it is somebody's own photograph of the night, and there is no version of
        // "your memory of the party was too big" that is the right answer. What that costs is storage,
        // which is what R2 (§1) is for.
        //
        // Three objects, three jobs: the shot as taken, a screen-sized copy to open, and a grid tile.
        // All three drop EXIF — which matters more here than anywhere else in the product, because
        // these are photographs OF other people's guests and the GPS tag in a camera roll would
        // otherwise publish where someone's wedding was to anyone who saves a picture of it.
        var original = imageOptimizer.Preserve(content, contentType);
        var view = imageOptimizer.Optimize(content, contentType, ViewEdge);
        var thumb = imageOptimizer.Optimize(content, contentType, ThumbEdge);

        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ExtensionFor(contentType);

        var id = Guid.NewGuid();
        var stem = $"campaigns/{campaign.Id:N}/photos/{id:N}";
        var originalUrl = await storage.PutAsync($"{stem}_o{ext}", original.Content, contentType, ct);
        var url = await storage.PutAsync($"{stem}{ext}", view.Content, contentType, ct);
        var thumbUrl = await storage.PutAsync($"{stem}_t{ext}", thumb.Content, contentType, ct);

        var photo = new EventPhoto
        {
            Id = id,
            CampaignId = campaign.Id,
            GuestId = guestId,
            UploaderName = guestId is null ? null : await GuestNameAsync(guestId.Value, ct),
            OriginalUrl = originalUrl,
            Url = url,
            ThumbUrl = thumbUrl,
            ContentType = contentType,
            SizeBytes = original.Content.Length + view.Content.Length + thumb.Content.Length,
            // The shot's own dimensions, not the viewing copy's — this is what the photo IS.
            Width = original.Width,
            Height = original.Height,
            CreatedAt = DateTimeOffset.UtcNow
        };

        await photos.AddAsync(photo, ct);
        await uow.SaveChangesAsync(ct);

        return new EventPhotoDto(
            photo.Id, photo.Url, photo.ThumbUrl, photo.OriginalUrl, photo.Width, photo.Height,
            photo.UploaderName, true, photo.CreatedAt);
    }

    public async Task DeleteAsync(
        Guid campaignId, Guid photoId, Guid? actingGuestId, CancellationToken ct = default)
    {
        var photo = await photos.Query(tracking: true)
            .FirstOrDefaultAsync(p => p.Id == photoId && p.CampaignId == campaignId, ct)
            ?? throw new NotFoundException("That photo is no longer here.");

        if (photo.DeletedAt is not null) return;   // already gone; deleting twice is not an error

        var mine = actingGuestId is { } me && photo.GuestId == me;
        if (!mine && !await MayHostAsync(campaignId, ct))
            throw new ForbiddenException("Only the host, or whoever took it, can remove this photo.");

        photo.DeletedAt = DateTimeOffset.UtcNow;
        photos.Update(photo);
        await uow.SaveChangesAsync(ct);
    }

    /// <summary>
    /// The host's door: holds the moderation right AND owns this campaign. Both halves matter — the
    /// permission says "may moderate a photo box", never "may moderate THIS one", and every customer
    /// account holds it.
    /// </summary>
    private async Task<bool> MayHostAsync(Guid campaignId, CancellationToken ct) =>
        currentUser.HasPermission(Permissions.Photos.Moderate) && await ownership.OwnsAsync(campaignId, ct);

    /// <summary>
    /// The guest's door. The id arrives from a caller that already authenticated it — the render
    /// cookie, or the OTP identity — but it is still checked against this campaign here, so a guest
    /// authenticated for one event cannot read another event's box by passing its id.
    /// </summary>
    private async Task<bool> IsGuestOfAsync(Guid campaignId, Guid? guestId, CancellationToken ct)
    {
        if (guestId is not { } id) return false;
        var guest = await guestRepository.GetByIdAsync(id, ct);
        return guest is not null && guest.CampaignId == campaignId;
    }

    /// <summary>
    /// The credit, read once and stored with the photo. Looked up through the repository rather than
    /// taken from the caller, so a guest cannot post under someone else's name.
    /// </summary>
    private async Task<string?> GuestNameAsync(Guid guestId, CancellationToken ct)
    {
        var guest = await guestRepository.GetByIdAsync(guestId, ct);
        return string.IsNullOrWhiteSpace(guest?.Name) ? null : guest.Name;
    }

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/avif" => ".avif",
        "image/heic" => ".heic",
        _ => ".img"
    };
}
