using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Pricing;

namespace InvitesBlog.Application.Services.Campaigns;

/// <summary>
/// Campaign builder + no-registration access business logic (§10.3 / §4.6). Ownership for
/// campaign-scoped actions is enforced here against <c>ICurrentUser.CampaignId</c>; token-in-URL
/// actions (dashboard) and anonymous actions (create, resend-link) validate/short-circuit inside.
/// </summary>
public interface ICampaignService
{
    Task<CreateCampaignResponse> CreateAsync(CreateCampaignRequest req, CancellationToken ct = default);
    Task UpdateContentAsync(Guid id, UpdateContentRequest req, CancellationToken ct = default);

    /// <summary>
    /// Sets the campaign's cover photo, or clears it when <paramref name="url"/> is null. Separate
    /// from <see cref="UpdateContentAsync"/> because the dashboard changes the cover without ever
    /// loading the rest of the content — routing it through the content save would have that screen
    /// post a blob it never read, overwriting whatever the builder last wrote.
    /// </summary>
    Task<CampaignImageDto> SetCoverAsync(Guid id, string? url, CancellationToken ct = default);

    /// <summary>
    /// Renames the campaign — the name the host files it under, not the title inside the invitation.
    /// </summary>
    Task RenameAsync(Guid id, RenameCampaignRequest req, CancellationToken ct = default);
    Task UpdateVenueAsync(Guid id, UpdateVenueRequest req, CancellationToken ct = default);

    Task<RsvpQuestionsResponse> GetRsvpQuestionsAsync(Guid id, CancellationToken ct = default);
    Task<RsvpQuestionsResponse> UpdateRsvpQuestionsAsync(
        Guid id, UpdateRsvpQuestionsRequest req, CancellationToken ct = default);
    Task UpdateInviterAsync(Guid id, UpdateInviterRequest req, string? accessToken, CancellationToken ct = default);
    Task UpdateDeliverySettingsAsync(Guid id, UpdateDeliverySettingsRequest req, CancellationToken ct = default);
    /// <summary>Finalize the campaign (no payment): returns the shareable /e/{id} link and emails it if chosen.</summary>
    Task<FinalizeResponse> FinalizeAsync(Guid id, CancellationToken ct = default);
    Task SetRolesAsync(Guid id, SetRolesRequest req, CancellationToken ct = default);
    Task<CampaignImageDto> AddImageAsync(Guid id, byte[] content, string contentType, string fileName, string? slot, CancellationToken ct = default);
    Task<CampaignSummaryDto> GetSummaryAsync(Guid id, CancellationToken ct = default);
    Task<PriceBreakdown> GetPricingAsync(Guid id, int? inviteCount, CancellationToken ct = default);
    Task ResendLinkAsync(ResendLinkRequest req, CancellationToken ct = default);
    Task<DashboardResponse> GetDashboardAsync(Guid id, string? token, CancellationToken ct = default);
    Task<CancelCampaignResponse> CancelAsync(Guid id, CancellationToken ct = default);
    Task<DeleteCampaignResponse> DeleteAsync(Guid id, CancellationToken ct = default);
}
