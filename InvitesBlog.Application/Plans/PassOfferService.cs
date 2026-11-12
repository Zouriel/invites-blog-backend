using System.Text.Json.Serialization;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// What an event's passes cost, and another year of them. Everyone pays the same: the venue discount
/// is gone (owner, 2026-11-12), since a venue's plan now covers its events itself.
/// </summary>
/// <param name="VenuePercent">Always 0. Kept for pages and app builds that still read it.</param>
/// <param name="VenueName">Always null, for the same reason.</param>
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

/// <summary>The price book's pass prices, the same for every event and every buyer.</summary>
public sealed class PassOfferService(IPriceBook priceBook) : IPassOfferService
{
    public async Task<PassOfferDto> ForCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var p = await priceBook.CurrentAsync(ct);
        return new PassOfferDto(p.GoldPass, p.PlatinumPass, p.GoldPass, p.PlatinumPass, p.GoldExtension, p.PlatinumExtension);
    }
}
