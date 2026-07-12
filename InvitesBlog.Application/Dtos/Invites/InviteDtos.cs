using System.Text.Json.Nodes;
using InvitesBlog.Domain.Entities;

namespace InvitesBlog.Application.Dtos.Invites;

// ----- Requests -----

/// <summary>Zero-login RSVP submitted against an invite token (§4.9.6).</summary>
public sealed record RsvpRequest(
    string Status, int? GuestCount, string? MealPreference, string? Comment, string? ArrivalTime, string? ContactNote);

// ----- by-token responses (returned as-is inside the ApiResponse envelope's Data; field names preserved) -----

public sealed record InviteCancelledResponse(bool Cancelled, string Message);
public sealed record InviteRequiresOtpResponse(bool RequiresOtp);
public sealed record InviteViewResponse(string PackageUrl, JsonObject Data, bool RequiresOtp, string CampaignStatus);

/// <summary>
/// The rendered invite for an OTP-authenticated guest opening the shared campaign link
/// (<c>/e/{campaignId}</c>). Carries the invite id + current RSVP so the client can RSVP.
/// </summary>
public sealed record MyInviteResponse(string PackageUrl, JsonObject Data, string CampaignStatus, Guid InviteId, string RsvpStatus);

// ----- other responses -----

public sealed record RsvpResultResponse(string Rsvp);
public sealed record InboxCardResponse(
    Guid InviteId, string EventTitle, DateTimeOffset EventDate, string VenueType,
    string RsvpStatus, bool IsNew, bool IsPast, bool Cancelled);
public sealed record ClaimResponse(bool Claimed);

/// <summary>
/// The resolved render payload the Application service needs to shape <see cref="InviteViewResponse"/>.
/// The actual build lives in Infrastructure (<c>InviteRenderService</c>); the controller — which can
/// see Infrastructure — supplies it through the <see cref="InviteRenderer"/> delegate so the
/// Application layer never takes a compile-time dependency on Infrastructure (which references it).
/// </summary>
public sealed record InviteRenderData(string PackageUrl, JsonObject Data, bool RequiresOtp, string CampaignStatus);

/// <summary>Bridges the Infrastructure invite renderer into the Application service.</summary>
public delegate InviteRenderData InviteRenderer(
    Campaign campaign, Template template, Guest guest, Invite invite, string inviteLink,
    string? inviterName, string? inviterPhone, string? inviterEmail);
