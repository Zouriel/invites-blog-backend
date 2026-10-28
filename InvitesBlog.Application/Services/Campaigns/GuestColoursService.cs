using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Campaigns;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Services.Campaigns;

/// <summary>The event's Colours page: the reply button and guest pages, and the QR table card. See <see cref="GuestColours"/>.</summary>
public interface IGuestColoursService
{
    Task<GuestColoursResponse> GetAsync(Guid campaignId, CancellationToken ct = default);
    Task<GuestColoursResponse> SetAsync(Guid campaignId, SetGuestColoursRequest req, CancellationToken ct = default);
}

public sealed class GuestColoursService(
    ICampaignOwnershipService ownership,
    ICampaignRepository campaigns,
    ITemplateRepository templates,
    IStorageService storage,
    IImageColourReader colourReader,
    IUnitOfWork uow) : IGuestColoursService
{
    /// <summary>Decoding a huge file only to suggest colours isn't worth it; the stored design is screen-sized anyway.</summary>
    private const int MaxReadBytes = 15 * 1024 * 1024;

    public async Task<GuestColoursResponse> GetAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await LoadOwnedAsync(campaignId, ct);
        return await ResponseAsync(campaign, ct);
    }

    public async Task<GuestColoursResponse> SetAsync(Guid campaignId, SetGuestColoursRequest req, CancellationToken ct = default)
    {
        if (!GuestColours.IsValid(req.Reply) || !GuestColours.IsValid(req.Card))
            throw new BusinessRuleException("Colours must be written like #c9a227.", "colour_invalid");

        var campaign = await LoadOwnedAsync(campaignId, ct);
        var template = await templates.GetByIdAsync(campaign.TemplateId, ct);

        // Reply colours only on an uploaded design. On a template the same three keys are its Theme
        // step's, and writing (or clearing) them here would quietly undo what the host chose there,
        // so the section is not offered for one and anything sent for it is ignored.
        if (template?.Visibility == TemplateVisibility.Imported)
            campaign.ThemeOverridesJson = GuestColours.WriteReply(campaign.ThemeOverridesJson, req.Reply);

        campaign.CardThemeJson = GuestColours.WriteCard(req.Card);
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        campaigns.Update(campaign);
        await uow.SaveChangesAsync(ct);
        return await ResponseAsync(campaign, ct);
    }

    private async Task<GuestColoursResponse> ResponseAsync(Campaign campaign, CancellationToken ct)
    {
        var template = await templates.GetByIdAsync(campaign.TemplateId, ct);
        var imported = template?.Visibility == TemplateVisibility.Imported;
        var image = imported && IsImage(template!.PreviewImageUrl) ? template.PreviewImageUrl : null;

        return new GuestColoursResponse(
            imported,
            campaign.Kind == CampaignKind.SaveTheDate,
            imported ? GuestColours.ReadReply(campaign.ThemeOverridesJson) : null,
            GuestColours.ReadCard(campaign.CardThemeJson),
            image is null ? [] : GuestColours.Suggest(await ReadColoursAsync(image, ct)),
            imported ? template!.PreviewImageUrl : null);
    }

    private async Task<ImageColours?> ReadColoursAsync(string url, CancellationToken ct)
    {
        var key = storage.KeyFor(url);
        if (key is null) return null;
        var bytes = await storage.GetAsync(key, ct);
        return bytes is null || bytes.Length == 0 || bytes.Length > MaxReadBytes ? null : colourReader.Read(bytes);
    }

    private static bool IsImage(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Path.GetExtension(url.Split('?')[0]).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp" or ".gif";

    private async Task<Campaign> LoadOwnedAsync(Guid id, CancellationToken ct)
    {
        if (!await ownership.OwnsAsync(id, ct)) throw new CampaignAccessDeniedException();
        return await campaigns.GetByIdAsync(id, ct) ?? throw new CampaignNotFoundException(id);
    }
}
