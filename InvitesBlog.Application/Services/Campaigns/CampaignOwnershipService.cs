using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Entities;

namespace InvitesBlog.Application.Services.Campaigns;

/// <summary>Answers "may this caller act on this campaign?" for every campaign-scoped action.</summary>
public interface ICampaignOwnershipService
{
    Task<bool> OwnsAsync(Guid campaignId, CancellationToken ct = default);
}

/// <summary>
/// A campaign has two legitimate keyholders, and both predate the other.
/// <list type="bullet">
/// <item>The POSSESSION token from the emailed dashboard link. No account behind it — that is the
/// whole point, and those links are already in people's inboxes.</item>
/// <item>The signed-in ACCOUNT that booked it. A campaign records the email/phone it was booked with
/// (as an Inviter), never a user id, so ownership is matched on the account's verified identifiers.
/// That also means an account picks up campaigns created before it existed.</item>
/// </list>
/// Previously only the first existed, so opening your own campaign while signed in was refused —
/// which is what "this dashboard link is missing its access token" was really saying.
/// </summary>
public sealed class CampaignOwnershipService(
    ICurrentUser currentUser,
    IRepository<AppUser> users,
    ICampaignRepository campaigns,
    IInviterRepository inviters) : ICampaignOwnershipService
{
    public async Task<bool> OwnsAsync(Guid campaignId, CancellationToken ct = default)
    {
        if (currentUser.CampaignId == campaignId) return true;
        if (currentUser.UserId is not { } userId) return false;

        var me = await users.GetByIdAsync(userId, ct);
        if (me is null) return false;
        if (string.IsNullOrWhiteSpace(me.Email) && string.IsNullOrWhiteSpace(me.PhoneE164)) return false;

        var campaign = await campaigns.GetByIdAsync(campaignId, ct);
        if (campaign?.InviterId is not { } inviterId) return false;

        var inviter = await inviters.GetByIdAsync(inviterId, ct);
        if (inviter is null) return false;

        return Matches(inviter.Email, me.Email) || Matches(inviter.PhoneE164, me.PhoneE164);
    }

    private static bool Matches(string? theirs, string? mine) =>
        !string.IsNullOrWhiteSpace(mine) && !string.IsNullOrWhiteSpace(theirs)
        && string.Equals(theirs, mine, StringComparison.OrdinalIgnoreCase);
}
