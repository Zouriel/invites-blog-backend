namespace InvitesBlog.Application.Dtos.Guidance;

/// <summary>
/// GET /api/me/guidance: tips/tours state, the Getting started progress and new celebrant roles.
/// The contract is docs/GUIDANCE.md section 2, shared with the web app and the Flutter app.
/// </summary>
/// <param name="TipsOn">False once the person turned tips off.</param>
/// <param name="Seen">Ids of tips, tours and offers already seen, oldest first (at most 500).</param>
/// <param name="WelcomeUntil">Account created + 30 days: welcome tips and tours stop after this.</param>
public sealed record GuidanceDto(
    bool TipsOn,
    IReadOnlyList<string> Seen,
    DateTimeOffset WelcomeUntil,
    GuidanceProgressDto Progress,
    IReadOnlyList<NewRoleDto> NewRoles);

/// <summary>
/// What the account has already done. "Hosted" below means an event the account organises: it
/// started it (Campaign.CreatedByUserId) or is its host (the campaign's Inviter has the account's
/// email or phone), the same two rules as /api/me/campaigns. Celebrant events don't count.
/// </summary>
/// <param name="HasName">The account's name is one the person gave, not a placeholder taken from
/// their email or phone at sign-up ("you", "invites.blog member", the email's local part, the phone).</param>
/// <param name="HasPhoto">The account has a profile picture (uploaded, or from Google/Apple).</param>
/// <param name="HasEvent">At least one hosted event, in any state (drafts and cancelled ones too).</param>
/// <param name="HasGuests">A hosted event has at least one guest (self-added ones included).</param>
/// <param name="HasSent">A hosted event has an invite that was sent or viewed (or emailed at least once),
/// a hand send (WhatsApp/Viber/Telegram), or an open link.</param>
/// <param name="HasPhotoInAlbum">A hosted event's album holds at least one photo or video that isn't deleted.</param>
/// <param name="HasDesign">The account has at least one design in the template designer (published or not).</param>
public sealed record GuidanceProgressDto(
    bool HasName, bool HasPhoto, bool HasEvent, bool HasGuests, bool HasSent, bool HasPhotoInAlbum, bool HasDesign);

/// <summary>
/// An event the account was added to as a celebrant and hasn't opened yet (CampaignCelebrant.AcknowledgedAt
/// is null). Matched on the account's email and phone, as every celebrant check is.
/// </summary>
/// <param name="Access">"look" (read-only) or "full" (may manage it with the organiser).</param>
/// <param name="AddedBy">The organiser's display name, or null when there is none to show.</param>
/// <param name="AddedAt">When they were added.</param>
public sealed record NewRoleDto(
    Guid CampaignId, string Title, string? CoverUrl, string Access, string? AddedBy, DateTimeOffset AddedAt);

/// <summary>POST /api/me/guidance/seen.</summary>
public sealed record MarkGuidanceSeenRequest(IReadOnlyList<string>? Ids);

/// <summary>PUT /api/me/guidance.</summary>
public sealed record SetGuidanceRequest(bool? TipsOn);
