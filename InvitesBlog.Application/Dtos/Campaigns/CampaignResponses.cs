using InvitesBlog.Application.Pricing;

namespace InvitesBlog.Application.Dtos.Campaigns;

// Response DTOs. Field names (camelCased on the wire) match the legacy anonymous responses so the
// Angular apps read the same JSON — only the ApiResponse envelope is added by the base controller.

/// <summary>Result of creating a draft campaign: the id, status, and the one-time access token.</summary>
public sealed record CreateCampaignResponse(Guid CampaignId, string Status, string AccessToken);

/// <summary>Template snippet embedded in the campaign summary.</summary>
public sealed record CampaignSummaryTemplateDto(string Name, string Slug, string PackageUrl, string ManifestJson);

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

// ----- Dashboard (§4.7.4 / §13.3) -----

public sealed record DashboardCampaignDto(Guid Id, string Title, string Status, int PaidInviteCapacity);

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
    string? DeliveryChannel);   // channel of the latest delivery attempt ("viber" / "email" / …)

public sealed record DashboardResponse(
    DashboardCampaignDto Campaign,
    DashboardReportDto Report,
    IReadOnlyList<DashboardGuestDto> Guests);
