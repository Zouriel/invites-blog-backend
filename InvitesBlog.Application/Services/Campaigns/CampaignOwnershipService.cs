using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Campaigns;

/// <summary>Answers "may this caller act on this campaign?" for every campaign-scoped action.</summary>
public interface ICampaignOwnershipService
{
    /// <summary>May act on this campaign: the organiser, or a celebrant given full access.</summary>
    Task<bool> OwnsAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>How far this caller reaches into the campaign. See <see cref="CampaignAccess"/>.</summary>
    Task<CampaignAccess> AccessAsync(Guid campaignId, CancellationToken ct = default);
}

/// <summary>What a caller may do with a campaign, from least to most.</summary>
public enum CampaignAccess
{
    None,
    /// <summary>A celebrant with the default read-only access: sees the event, changes nothing.</summary>
    Celebrant,
    /// <summary>
    /// A celebrant the organiser gave full access, or the owner or staff of the venue the event is
    /// held at. Runs the event like the organiser.
    /// </summary>
    Manager,
    /// <summary>Whoever organised it: the possession token, the account that started it, or its host.</summary>
    Organiser,
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
    IInviterRepository inviters,
    IRepository<CampaignCelebrant> celebrants,
    IRepository<Venue> venues,
    IRepository<VenueStaff> venueStaff) : ICampaignOwnershipService
{
    public async Task<bool> OwnsAsync(Guid campaignId, CancellationToken ct = default) =>
        await AccessAsync(campaignId, ct) >= CampaignAccess.Manager;

    public async Task<CampaignAccess> AccessAsync(Guid campaignId, CancellationToken ct = default)
    {
        if (currentUser.CampaignId == campaignId) return CampaignAccess.Organiser;
        if (currentUser.UserId is not { } userId) return CampaignAccess.None;

        var me = await users.GetByIdAsync(userId, ct);
        if (me is null) return CampaignAccess.None;
        if (string.IsNullOrWhiteSpace(me.Email) && string.IsNullOrWhiteSpace(me.PhoneE164)) return CampaignAccess.None;

        var campaign = await campaigns.GetByIdAsync(campaignId, ct);
        if (campaign is null) return CampaignAccess.None;

        // Whoever started it owns it, even before a host is attached. Without this a draft abandoned
        // at the first step could be listed but never opened or deleted — no inviter to match on.
        if (campaign.CreatedByUserId == userId) return CampaignAccess.Organiser;

        if (campaign.InviterId is { } inviterId
            && await inviters.GetByIdAsync(inviterId, ct) is { } inviter
            && (Matches(inviter.Email, me.Email) || Matches(inviter.PhoneE164, me.PhoneE164)))
            return CampaignAccess.Organiser;

        // Somebody the event is for, matched on the contacts their account has proved.
        var email = me.Email?.Trim().ToLowerInvariant();
        var phone = me.PhoneE164?.Trim();
        var celebrant = await celebrants.Query()
            .Where(c => c.CampaignId == campaignId
                        && ((email != null && c.Email == email) || (phone != null && c.PhoneE164 == phone)))
            .OrderByDescending(c => c.CanManage)
            .FirstOrDefaultAsync(ct);

        if (celebrant is { CanManage: true }) return CampaignAccess.Manager;

        // An event at a venue is run by the venue: its owner, and its staff by their proved email.
        if (campaign.VenueId is { } venueId && await RunsVenueAsync(venueId, userId, email, ct))
            return CampaignAccess.Manager;

        return celebrant is null ? CampaignAccess.None : CampaignAccess.Celebrant;
    }

    private async Task<bool> RunsVenueAsync(Guid venueId, Guid userId, string? email, CancellationToken ct) =>
        await venues.Query().AnyAsync(v => v.Id == venueId && v.OwnerUserId == userId, ct)
        || (email is not null && await venueStaff.Query().AnyAsync(s => s.VenueId == venueId && s.Email == email, ct));

    private static bool Matches(string? theirs, string? mine) =>
        !string.IsNullOrWhiteSpace(mine) && !string.IsNullOrWhiteSpace(theirs)
        && string.Equals(theirs, mine, StringComparison.OrdinalIgnoreCase);
}
