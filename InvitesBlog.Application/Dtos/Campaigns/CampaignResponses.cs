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
    PriceBreakdown Price,
    /// <summary>
    /// Whether the customer brought this design themselves. What the builder branches on: an
    /// imported design declares no fields, so the steps that fill fields — and Venue and RSVP,
    /// whose answers would have nowhere to appear — are not shown for one.
    /// </summary>
    bool IsImported = false,
    /// <summary>
    /// The event's open link, or null when it has none. The whole URL rather than a boolean,
    /// because the host needs to copy it again tomorrow and the code is the only place it lives.
    /// </summary>
    string? OpenLink = null);

/// <summary>Result of uploading a campaign image — the stored public URL to bind to a template image slot.</summary>
public sealed record CampaignImageDto(string Url);

/// <summary>
/// An event's open link, as the builder shows it back.
/// </summary>
/// <param name="Url">
/// The whole address to share. An anonymous one carries its code and is also readable later from the
/// event's dashboard — the code is stored in the clear precisely so a host can come back for their
/// own public link without minting a new one and killing the link they already shared. A gated one
/// is just <c>/e/{id}</c>, which needs no code because it authenticates whoever follows it.
/// </param>
/// <param name="AllowsAnonymous">
/// Which kind came back, so the page can say what it hands out rather than inferring it from the
/// shape of the URL.
/// </param>
public sealed record OpenLinkResponse(string Url, bool AllowsAnonymous);

/// <param name="AllowAnonymous">
/// Whether whoever follows this link may open the invitation without proving who they are.
///
/// <para>False does NOT mean "no link" — it means the gated one, <c>/e/{id}</c>, which asks the
/// visitor for an email or phone that is on the guest list and mails them a code. Both are public
/// addresses the host pastes somewhere; they differ only in what is asked at the door. An event with
/// no guest list and this set to false therefore produces a link that opens for nobody, which is
/// why the page says so beside the box.</para>
/// </param>
public sealed record SetOpenLinkRequest(bool AllowAnonymous);

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
    bool HasInvitation = true,
    /// <summary>
    /// The event's open link, or null. Carried here so a host can copy the address they are
    /// sharing without walking back through the builder — it is stored in the clear precisely so
    /// this is possible; see <c>Campaign.OpenLinkCode</c>.
    /// </summary>
    string? OpenLink = null,
    /// <summary>
    /// Whether the customer brought this design themselves. The dashboard offers the open link only
    /// for one — a gallery template's whole value is the per-guest personalisation an anonymous
    /// viewer cannot be given, and the server refuses it there anyway.
    /// </summary>
    bool IsImported = false,
    /// <summary>
    /// Whether this event is still an unfinished draft — a design uploaded, or a template chosen,
    /// but never sent.
    ///
    /// <para>Not the same question as <see cref="DashboardCampaignDto.HasInvitation"/>, and the gap
    /// between them is what made an unfinished import invisible: a package URL is written the moment
    /// artwork is uploaded, so "has an invitation" went true while the host was still three steps
    /// from finishing. The dashboard needs both — one to decide whether to offer an invitation at
    /// all, the other to offer finishing the one that exists.</para>
    /// </summary>
    bool IsDraft = false);

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
