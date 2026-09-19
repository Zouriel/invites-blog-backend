using System.Text.Json.Nodes;
using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Domain.Entities;

namespace InvitesBlog.Application.Dtos.Invites;

// ----- Requests -----

/// <summary>Zero-login RSVP submitted against an invite token (§4.9.6).</summary>
public sealed record RsvpRequest(
    string Status, int? GuestCount, string? MealPreference, string? Comment, string? ArrivalTime,
    string? ContactNote, IReadOnlyDictionary<string, string>? Answers = null);

// ----- by-token responses (returned as-is inside the ApiResponse envelope's Data; field names preserved) -----

public sealed record InviteCancelledResponse(bool Cancelled, string Message);
public sealed record InviteRequiresOtpResponse(bool RequiresOtp);
public sealed record InviteViewResponse(
    string PackageUrl, JsonObject Data, bool RequiresOtp, string CampaignStatus,
    IReadOnlyList<RsvpQuestionDto>? RsvpQuestions = null,
    // Additive: the server-rendered guest path mints its cookie from this rather than looking the
    // invite up a second time. Existing clients simply ignore it.
    Guid InviteId = default);

/// <summary>
/// A reauth code was sent for a personal invite link opened from an untrusted IP.
/// <paramref name="Channel"/> ("email" or "sms") lets the UI say where to look without exposing the
/// actual address — same privacy posture as the rest of the OTP flow.
/// </summary>
public sealed record InviteReauthRequestedResponse(Guid ChallengeId, int ExpiresInSeconds, string Channel);

// ----- other responses -----

/// <summary>
/// The three colours the guest's own invitation is painted with, for the plain pages either side of
/// it (RSVP, the photo box). Any of them may be null when the template never declared it and the
/// inviter never picked one — callers keep their own default for that.
/// </summary>
public sealed record GuestThemeResponse(string? Accent, string? Background, string? Text);

public sealed record RsvpResultResponse(string Rsvp);
public sealed record InboxCardResponse(
    Guid InviteId, Guid CampaignId, string EventTitle, DateTimeOffset EventDate, string VenueType,
    string RsvpStatus, bool IsNew, bool IsPast, bool Cancelled, string? InviterName = null,
    string? PreviewImageUrl = null, int PhotoCount = 0,
    /// <summary>"invitation" or "saveTheDate"; a save the date has no reply and carries calendar links.</summary>
    string Kind = "invitation",
    CalendarLinksDto? Calendar = null);

/// <summary>Add to calendar, pre-filled: Google, Outlook.com and Microsoft 365.</summary>
public sealed record CalendarLinksDto(string Google, string Outlook, string Office365);

/// <summary>
/// The resolved render payload the Application service needs to shape <see cref="InviteViewResponse"/>.
/// The actual build lives in Infrastructure (<c>InviteRenderService</c>); the controller — which can
/// see Infrastructure — supplies it through the <see cref="InviteRenderer"/> delegate so the
/// Application layer never takes a compile-time dependency on Infrastructure (which references it).
/// </summary>
public sealed record InviteRenderData(
    string PackageUrl, JsonObject Data, bool RequiresOtp, string CampaignStatus, GuestCredit? Credit = null);

/// <summary>
/// The small line a guest-facing page carries: "Made with invites.blog" on a Free event, the Studio
/// designer who made the invitation, and the venue whose albums they are.
/// </summary>
public sealed record GuestCredit(bool MadeWith, string? DesignedBy, string? VenueName, string? VenueLogoUrl)
{
    public bool IsEmpty => !MadeWith && DesignedBy is null && VenueName is null;
}

/// <summary>Bridges the Infrastructure invite renderer into the Application service.</summary>
/// <param name="bucketWindowDays">
/// How many days the event's default media bucket collects for. Passed IN rather than looked up by
/// the renderer, because the renderer is synchronous and the number lives behind a query — and
/// because it must be the same number the bucket itself enforces. A camera offered on an invitation
/// that leads to a bucket refusing every photograph taken with it is precisely what one shared
/// window exists to prevent.
/// </param>
public delegate InviteRenderData InviteRenderer(
    Campaign campaign, Template template, Guest guest, Invite invite, string inviteLink,
    string? inviterName, string? inviterPhone, string? inviterEmail, int bucketWindowDays);

/// <summary>The camera on a static invitation. See <c>IInviteService.StaticCameraAsync</c>.</summary>
public sealed record StaticCameraInfo(
    Guid CampaignId, Guid BucketId, string EventTitle, bool IsStatic, bool IsOpen, bool IsCancelled);
