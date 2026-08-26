using InvitesBlog.Application.Dtos.Invites;
using InvitesBlog.Application.Dtos.Otp;

namespace InvitesBlog.Application.Services.Invites;

/// <summary>Invitee token view / RSVP / inbox / claim (§10.8).</summary>
public interface IInviteService
{
    /// <summary>
    /// Resolves an invite by token. Returns one of three shapes (as <see cref="object"/>, preserved
    /// verbatim in the response Data): cancelled, requires-OTP, or the rendered view. The caller
    /// supplies <paramref name="render"/> so the payload can be built in Infrastructure.
    /// <paramref name="ipAddress"/> is the personal-link IP-binding check (see
    /// <see cref="RequestReauthAsync"/>): the first-ever open of an invite auto-trusts its IP,
    /// requires-OTP is returned for any later open from an IP not among the (up to 3) already trusted.
    /// </summary>
    Task<object> GetByTokenAsync(string token, string? ipAddress, InviteRenderer render, CancellationToken ct = default);
    /// <summary>Rendered invite for the OTP-authenticated caller via the shared campaign link (<c>/e/{id}</c>).</summary>
    Task<object> GetMyInviteAsync(Guid campaignId, InviteRenderer render, CancellationToken ct = default);

    /// <summary>
    /// Which guest the caller IS on this campaign, matched on every verified identifier they hold —
    /// or null if they are not on its guest list. The photo box is authorized on this: being a guest
    /// of the event is what earns the right to see what everyone shot at it.
    /// </summary>
    Task<Guid?> MyGuestIdAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>
    /// The campaign and guest an invite belongs to. Like <see cref="RenderAuthorizedAsync"/> it does
    /// no access check of its own — it is for a caller whose admission already happened, and exists so
    /// the server-rendered guest path can name the guest it is acting as without re-deriving them
    /// from a token it no longer holds. Null when the invite has gone.
    /// </summary>
    Task<(Guid CampaignId, Guid GuestId)?> InviteSubjectAsync(Guid inviteId, CancellationToken ct = default);

    /// <summary>
    /// Builds the payload for an invite the caller has ALREADY been authorized to see. Does no access
    /// check of its own — it is the tail of a flow whose head did the checking, which is why it takes
    /// an id rather than a token. The server-rendered guest path uses it: admission happens once at
    /// <c>/i/{token}</c> and is carried afterwards by an HttpOnly cookie, so the render itself has no
    /// token to re-check. Returns null when the invite, its campaign or its template has gone.
    /// </summary>
    /// <param name="inviteLink">
    /// The URL this invitation is being served at. It becomes <c>invite.link</c>, and
    /// <c>rsvp.link</c> is derived from it, so the template's own RSVP button stays on whatever path
    /// the guest actually arrived by.
    /// </param>
    Task<InviteRenderData?> RenderAuthorizedAsync(
        Guid inviteId, string inviteLink, InviteRenderer render, CancellationToken ct = default);

    /// <summary>
    /// Records an RSVP for an invite the caller has ALREADY been authorized to answer for — the
    /// cookie-carried counterpart of <see cref="RsvpAsync"/>. Like
    /// <see cref="RenderAuthorizedAsync"/> it performs no access check of its own.
    /// </summary>
    Task<RsvpResultResponse> RsvpAuthorizedAsync(Guid inviteId, RsvpRequest req, CancellationToken ct = default);

    Task<RsvpResultResponse> RsvpAsync(string token, string? ipAddress, RsvpRequest req, CancellationToken ct = default);
    /// <summary>Authenticated RSVP from the inbox (by invite id, ownership-checked).</summary>
    Task<RsvpResultResponse> RsvpByInviteIdAsync(Guid inviteId, RsvpRequest req, CancellationToken ct = default);
    Task<IReadOnlyList<InboxCardResponse>> GetInboxAsync(CancellationToken ct = default);
    /// <summary>Claim an invite to the caller's inbox — authorized by possession of the raw token.</summary>
    Task<ClaimResponse> ClaimAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Sends a reauth OTP for a personal invite link opened from an untrusted IP. The caller never
    /// supplies a contact — the link is already user-bound, so the code goes to whatever contact the
    /// guest row has on file (email preferred, phone as fallback).
    /// </summary>
    Task<InviteReauthRequestedResponse> RequestReauthAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// Verifies the reauth code, trusts <paramref name="ipAddress"/> for this invite (evicting the
    /// least-recently-seen of the existing 3 if already full), and returns the same shape
    /// <see cref="GetByTokenAsync"/> would once the IP is trusted.
    /// </summary>
    Task<object> VerifyReauthAsync(
        string token, string? ipAddress, VerifyOtpRequest req, InviteRenderer render, CancellationToken ct = default);
}
