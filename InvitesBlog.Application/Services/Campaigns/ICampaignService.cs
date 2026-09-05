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
    /// A campaign with no invitation behind it — one that exists for a media bucket, or for a design
    /// the customer brought themselves.
    ///
    /// <para>Ordinary creation pins a template, and neither of those has one to pin. A placeholder is
    /// made here rather than by each caller so that campaign creation — tokens, ownership, slug,
    /// status — stays in one place and cannot drift between the two ways in.</para>
    /// </summary>
    /// <param name="eventDate">
    /// The night it is for, applied here rather than through <see cref="SetEventDateAsync"/> because
    /// that one checks ownership and the caller may own nothing yet: the possession token that makes
    /// this campaign theirs is minted by this very call and handed back in its response, so it is not
    /// on the request being served. Checking would refuse everybody who is not already signed in —
    /// which is most of the people the unguarded create page exists for.
    /// </param>
    Task<CreateCampaignResponse> CreateBareAsync(
        string title, DateTimeOffset? eventDate = null, CancellationToken ct = default);

    /// <summary>
    /// Sets the night an event is for. Normalised to UTC before storing — a browser sends whatever
    /// offset it is in, and Npgsql accepts none but UTC for `timestamp with time zone`.
    /// </summary>
    Task SetEventDateAsync(Guid campaignId, DateTimeOffset when, CancellationToken ct = default);

    /// <summary>
    /// Gives an event that has no invitation one, by pinning a template onto it.
    ///
    /// <para>An event may be an invitation, a media bucket, or both — and until this existed the
    /// choice was made once, by whichever door somebody came through, and could never be revisited.
    /// A bucket bought for a trip could never become an invitation, which made "or both" untrue for
    /// everybody who did not pick both at the start.</para>
    ///
    /// <para>Refuses an event that ALREADY has an invitation. Campaigns pin their package precisely
    /// so that what was sent stays what was sent; swapping the design under a campaign whose invites
    /// are in people's inboxes would re-render every one of them.</para>
    /// </summary>
    Task<CampaignSummaryDto> AttachTemplateAsync(
        Guid campaignId, Guid templateId, CancellationToken ct = default);

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
