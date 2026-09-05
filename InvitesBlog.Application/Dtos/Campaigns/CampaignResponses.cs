using InvitesBlog.Application.Pricing;

namespace InvitesBlog.Application.Dtos.Campaigns;

// Response DTOs. Field names (camelCased on the wire) match the legacy anonymous responses so the
// Angular apps read the same JSON — only the ApiResponse envelope is added by the base controller.

/// <summary>Result of creating a draft campaign: the id, status, and the one-time access token.</summary>
public sealed record CreateCampaignResponse(Guid CampaignId, string Status, string AccessToken);

/// <summary>Template snippet embedded in the campaign summary.</summary>
/// <param name="PreviewImageUrl">
/// The template's own marketing poster. Shown in the builder ONLY as "this is what your invitation
/// falls back to without a cover" — it is rendered from the template's demo content, so it carries
/// example names, and the whole point of showing it is that the host sees why to replace it.
/// </param>
public sealed record CampaignSummaryTemplateDto(
    string Name, string Slug, string PackageUrl, string ManifestJson, string? PreviewImageUrl = null);

/// <summary>The full campaign builder summary (§10.3 GET summary).</summary>
public sealed record CampaignSummaryDto(
    Guid Id,
    string Title,
    string Slug,
    string Status,
    string EventType,
    DateTimeOffset EventStartAt,
    DateTimeOffset? EventEndAt,
    int PaidInviteCapacity,
    bool HasDesignerDiscount,
    bool IsSensitive,
    string CustomContentJson,
    string ThemeOverridesJson,
    string RulesJson,
    string RolesJson,
    string DeliverySettingsJson,
    int GuestCount,
    CampaignSummaryTemplateDto? Template,
    PriceBreakdown Price);

/// <summary>Result of uploading a campaign image — the stored public URL to bind to a template image slot.</summary>
public sealed record CampaignImageDto(string Url);

/// <summary>Result of cancelling a campaign (§14.3).</summary>
public sealed record CancelCampaignResponse(bool Cancelled, bool Refunded, string? Note = null);

/// <summary>Result of hard-deleting a campaign (§15.5).</summary>
public sealed record DeleteCampaignResponse(bool Deleted);

/// <summary>Result of finalizing a campaign: the shareable link + how many guests were emailed it.</summary>
public sealed record FinalizeResponse(string ShareLink, int GuestCount, int Emailed);

// ----- Dashboard (§4.7.4 / §13.3) -----

public sealed record DashboardCampaignDto(
    Guid Id, string Title, string Status, int PaidInviteCapacity,
    /// <summary>Raw roles blob, same shape/parsing as the builder summary — lets the dashboard offer
    /// a role picker on "Add guest" instead of free text.</summary>
    string RolesJson,
    /// <summary>The host's chosen cover, or null when they haven't set one.</summary>
    string? CoverImageUrl = null,
    /// <summary>
    /// What the tile falls back to without a cover — the TEMPLATE's marketing poster, rendered from
    /// its demo content. Shown in the picker so the host can see why it is worth replacing.
    /// </summary>
    string? TemplatePreviewImageUrl = null,
    /// <summary>
    /// Whether this event has an invitation at all. An event may be a bucket on its own — somebody
    /// who only wanted the photographs — and the dashboard has to know, or it offers guest tables and
    /// a "send" for something that has nothing to send.
    /// </summary>
    bool HasInvitation = true);

public sealed record DashboardRsvpDto(int Going, int Maybe, int NotGoing);

public sealed record DashboardReportDto(int Total, int Sent, int Failed, int Viewed, int NotSent, DashboardRsvpDto Rsvp);

public sealed record DashboardGuestDto(
    Guid Id,
    string Name,
    string? Email,
    string? PhoneE164,
    string? Role,
    string Gender,
    bool OptedOut,
    string InviteStatus,
    string RsvpStatus,
    DateTimeOffset? ViewedAt,
    string? DeliveryChannel,   // channel of the latest delivery attempt ("viber" / "email" / …)
    /// <summary>Their latest answers, keyed by question. Empty until they reply.</summary>
    IReadOnlyDictionary<string, string>? RsvpAnswers = null);

public sealed record DashboardResponse(
    DashboardCampaignDto Campaign,
    DashboardReportDto Report,
    IReadOnlyList<DashboardGuestDto> Guests,
    /// <summary>What was asked, so the table can put answers under the right headings.</summary>
    IReadOnlyList<RsvpQuestionDto>? RsvpQuestions = null);
