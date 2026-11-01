using System.Text.Json.Serialization;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// What an event's passes cost its host, and why. The one discount is a venue's: an event a venue
/// runs gets the venue discount on its passes and on another year of them. Everything else is full price.
/// </summary>
/// <param name="VenuePercent">The venue discount on an event a venue runs (passes and extensions); 0 otherwise.</param>
/// <param name="VenueName">That venue.</param>
public sealed record PassOfferDto(
    decimal GoldPass, decimal PlatinumPass, decimal FullGoldPass, decimal FullPlatinumPass,
    decimal GoldExtension, decimal PlatinumExtension,
    int VenuePercent = 0, string? VenueName = null)
{
    // The same prices under their names before the rename, read by web tabs still open from before
    // that deploy. Nothing current reads them; remove them a release later.
    [JsonPropertyName("partyPass")] public decimal OldGoldPass => GoldPass;
    [JsonPropertyName("weddingPass")] public decimal OldPlatinumPass => PlatinumPass;
    [JsonPropertyName("fullPartyPass")] public decimal OldFullGoldPass => FullGoldPass;
    [JsonPropertyName("fullWeddingPass")] public decimal OldFullPlatinumPass => FullPlatinumPass;
    [JsonPropertyName("partyExtension")] public decimal OldGoldExtension => GoldExtension;
    [JsonPropertyName("weddingExtension")] public decimal OldPlatinumExtension => PlatinumExtension;

    public decimal PassPrice(EventPassKind kind) => kind == EventPassKind.Platinum ? PlatinumPass : GoldPass;
    public decimal ExtensionPrice(EventPassKind kind) => kind == EventPassKind.Platinum ? PlatinumExtension : GoldExtension;
}

public interface IPassOfferService
{
    Task<PassOfferDto> ForCampaignAsync(Guid campaignId, CancellationToken ct = default);
}

public sealed class PassOfferService(
    ICampaignRepository campaigns,
    IRepository<Domain.Entities.AppUser> users,
    IPriceBook priceBook,
    IRepository<Domain.Entities.Venue>? venues = null) : IPassOfferService
{
    public async Task<PassOfferDto> ForCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var p = await priceBook.CurrentAsync(ct);
        var campaign = await campaigns.GetByIdAsync(campaignId, ct);
        var offer = new PassOfferDto(p.GoldPass, p.PlatinumPass, p.GoldPass, p.PlatinumPass, p.GoldExtension, p.PlatinumExtension);

        // An event a venue runs: the venue buys its pass, and another year of it, at the venue price,
        // while the venue's plan is in force.
        if (campaign?.VenueId is { } venueId && venues is not null
            && await venues.Query().FirstOrDefaultAsync(v => v.Id == venueId, ct) is { } venue
            && await users.Query().AnyAsync(u => u.Id == venue.OwnerUserId && u.SubscriptionTier == SubscriptionTier.Venue
                                                 && (u.SubscriptionEndsAt == null || u.SubscriptionEndsAt > DateTimeOffset.UtcNow), ct)
            && p.VenueDiscountPercent > 0)
            offer = offer with
            {
                GoldPass = p.VenuePassPrice(EventPassKind.Gold),
                PlatinumPass = p.VenuePassPrice(EventPassKind.Platinum),
                GoldExtension = p.VenueExtensionPrice(EventPassKind.Gold),
                PlatinumExtension = p.VenueExtensionPrice(EventPassKind.Platinum),
                VenuePercent = p.VenueDiscountPercent,
                VenueName = venue.Name,
            };
        return offer;
    }
}
