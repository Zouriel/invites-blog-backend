using InvitesBlog.Application.Dtos.Invites;

namespace InvitesBlog.Application.Services.Invites;

/// <summary>Invitee token view / RSVP / inbox / claim (§10.8).</summary>
public interface IInviteService
{
    /// <summary>
    /// Resolves an invite by token. Returns one of three shapes (as <see cref="object"/>, preserved
    /// verbatim in the response Data): cancelled, requires-OTP, or the rendered view. The caller
    /// supplies <paramref name="render"/> so the payload can be built in Infrastructure.
    /// </summary>
    Task<object> GetByTokenAsync(string token, InviteRenderer render, CancellationToken ct = default);

    Task<RsvpResultResponse> RsvpAsync(string token, RsvpRequest req, CancellationToken ct = default);
    /// <summary>Authenticated RSVP from the inbox (by invite id, ownership-checked).</summary>
    Task<RsvpResultResponse> RsvpByInviteIdAsync(Guid inviteId, RsvpRequest req, CancellationToken ct = default);
    Task<IReadOnlyList<InboxCardResponse>> GetInboxAsync(CancellationToken ct = default);
    /// <summary>Claim an invite to the caller's inbox — authorized by possession of the raw token.</summary>
    Task<ClaimResponse> ClaimAsync(string token, CancellationToken ct = default);
}
