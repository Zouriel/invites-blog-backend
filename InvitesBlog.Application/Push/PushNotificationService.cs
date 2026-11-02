using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Push;

/// <summary>A notification for one account: short plain words, and the app path its tap opens.</summary>
public sealed record PushNote(Guid UserId, string Title, string Body, string Path);

/// <summary>
/// Turns a <see cref="PushJob"/> into notifications and sends them. Runs on the background worker with
/// a scope of its own, after the action that raised the job has been saved. Who gets one mirrors who
/// already sees the thing on the website (inbox, dashboard, feed), matched to accounts the same way
/// the rest of the site matches them: by the email or phone number the account has proved.
/// </summary>
public sealed class PushNotificationService(
    ICampaignRepository campaigns,
    IGuestRepository guests,
    IInviteRepository invites,
    IInviterRepository inviters,
    IRepository<AppUser> users,
    IRepository<CampaignCelebrant> celebrants,
    IRepository<PostComment> comments,
    IUnitOfWork uow,
    IPushSender sender)
{
    /// <summary>Longest stretch of a comment quoted in a notification.</summary>
    public const int QuoteLength = 80;

    public async Task HandleAsync(PushJob job, CancellationToken ct)
    {
        var notes = await NotesAsync(job, ct);
        foreach (var note in notes)
        {
            if (note.UserId == job.ActorUserId) continue;
            await sender.SendAsync(note.UserId, note.Title, note.Body,
                new Dictionary<string, string> { ["path"] = note.Path }, ct);
        }
    }

    /// <summary>What <see cref="HandleAsync"/> would send, one per account, before the actor is left out.</summary>
    public async Task<IReadOnlyList<PushNote>> NotesAsync(PushJob job, CancellationToken ct) => job switch
    {
        InvitationsSent j => await InvitationsAsync(j, ct),
        GuestReplied j => await ReplyAsync(j, ct),
        CommentAdded j => await CommentAsync(j, ct),
        CelebrantAdded j => await CelebrantAsync(j, ct),
        _ => [],
    };

    // ----- Invitations ---------------------------------------------------------------------------

    private async Task<IReadOnlyList<PushNote>> InvitationsAsync(InvitationsSent job, CancellationToken ct)
    {
        var campaign = await campaigns.GetByIdAsync(job.CampaignId, ct);
        if (campaign is null || GuestVisibility.Hidden.Contains(campaign.Status) || campaign.Status == CampaignStatus.Cancelled)
            return [];

        var ids = job.GuestIds.Distinct().ToList();
        // Once per guest, ever: a resend or a second send of the same event doesn't buzz them again.
        var due = await guests.Query(tracking: true)
            .Where(g => ids.Contains(g.Id) && g.CampaignId == job.CampaignId && !g.OptedOut && g.InvitePushedAt == null)
            .ToListAsync(ct);
        if (due.Count == 0) return [];

        var now = DateTimeOffset.UtcNow;
        foreach (var g in due) g.InvitePushedAt = now;
        await uow.SaveChangesAsync(ct);

        // The people running the event never get "you're invited" to it, even if they listed themselves.
        var organisers = (await OrganisersAsync(campaign, ct)).ToHashSet();
        var host = campaign.InviterId is { } inviterId ? (await inviters.GetByIdAsync(inviterId, ct))?.Name : null;
        var saveTheDate = SaveTheDates.Is(campaign);
        var title = saveTheDate ? "Save the date" : "You're invited";
        var body = (saveTheDate, string.IsNullOrWhiteSpace(host)) switch
        {
            (true, false) => $"{host.Trim()} sent you a save the date for {campaign.Title}.",
            (true, true) => $"A save the date for {campaign.Title}.",
            (false, false) => $"{host.Trim()} invited you to {campaign.Title}.",
            (false, true) => $"You're invited to {campaign.Title}.",
        };

        var notes = new List<PushNote>();
        var seen = new HashSet<Guid>();
        foreach (var g in due)
            foreach (var userId in await AccountsAsync(g.Email, g.PhoneE164, ct))
                if (!organisers.Contains(userId) && seen.Add(userId))
                    notes.Add(new PushNote(userId, title, body, $"/invitation/{campaign.Id}"));
        return notes;
    }

    // ----- Replies -------------------------------------------------------------------------------

    private async Task<IReadOnlyList<PushNote>> ReplyAsync(GuestReplied job, CancellationToken ct)
    {
        if (job.Status is not (RsvpStatus.Going or RsvpStatus.Maybe or RsvpStatus.NotGoing)) return [];
        var invite = await invites.GetByIdAsync(job.InviteId, ct);
        if (invite is null) return [];
        var guest = await guests.GetByIdAsync(invite.GuestId, ct);
        var campaign = await campaigns.GetByIdAsync(invite.CampaignId, ct);
        if (guest is null || campaign is null) return [];

        var name = string.IsNullOrWhiteSpace(guest.Name) ? "A guest" : guest.Name.Trim();
        var body = job.Status switch
        {
            RsvpStatus.Going => $"{name} is coming.",
            RsvpStatus.Maybe => $"{name} might come.",
            _ => $"{name} can't make it.",
        };

        // Whoever answered doesn't hear about their own answer, signed in or not.
        var replier = (await AccountsAsync(guest.Email, guest.PhoneE164, ct)).ToHashSet();
        return (await PeopleOfAsync(campaign, ct))
            .Where(id => !replier.Contains(id))
            .Select(id => new PushNote(id, campaign.Title, body, $"/dashboard/{campaign.Id}"))
            .ToList();
    }

    // ----- Comments ------------------------------------------------------------------------------

    private async Task<IReadOnlyList<PushNote>> CommentAsync(CommentAdded job, CancellationToken ct)
    {
        var comment = await comments.GetByIdAsync(job.CommentId, ct);
        if (comment is null || comment.DeletedAt is not null) return [];
        var campaign = await campaigns.GetByIdAsync(comment.CampaignId, ct);
        if (campaign is null || campaign.Status == CampaignStatus.Cancelled) return [];

        var author = await users.GetByIdAsync(comment.UserId, ct);
        var who = string.IsNullOrWhiteSpace(author?.DisplayName) ? "Someone" : author!.DisplayName.Trim();
        var quote = Quote(comment.Body);
        var path = $"/feed?post={campaign.Id}";

        var notes = new Dictionary<Guid, PushNote>();
        // Somebody answered your comment: that's the more personal of the two, so it wins.
        if (comment.ParentId is { } parentId && await comments.GetByIdAsync(parentId, ct) is { DeletedAt: null } parent)
            notes[parent.UserId] = new PushNote(parent.UserId, campaign.Title, $"{who} replied: {quote}", path);

        // The post is the event's, so it is the organisers' post.
        foreach (var id in await OrganisersAsync(campaign, ct))
            notes.TryAdd(id, new PushNote(id, campaign.Title, $"{who} commented: {quote}", path));
        return notes.Values.ToList();
    }

    internal static string Quote(string body)
    {
        var flat = string.Join(' ', body.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= QuoteLength ? flat : flat[..(QuoteLength - 1)].TrimEnd() + "…";
    }

    // ----- Celebrants ----------------------------------------------------------------------------

    private async Task<IReadOnlyList<PushNote>> CelebrantAsync(CelebrantAdded job, CancellationToken ct)
    {
        var celebrant = await celebrants.GetByIdAsync(job.CelebrantId, ct);
        if (celebrant is null) return [];
        var campaign = await campaigns.GetByIdAsync(celebrant.CampaignId, ct);
        if (campaign is null || campaign.Status == CampaignStatus.Cancelled) return [];

        return (await AccountsAsync(celebrant.Email, celebrant.PhoneE164, ct))
            .Select(id => new PushNote(id, $"You've been added to {campaign.Title}",
                "See who's coming and all the photos.", $"/dashboard/{campaign.Id}"))
            .ToList();
    }

    // ----- Who --------------------------------------------------------------------------------------

    /// <summary>Accounts that have proved this email or phone number.</summary>
    private async Task<IReadOnlyList<Guid>> AccountsAsync(string? email, string? phone, CancellationToken ct)
    {
        var mail = string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();
        var tel = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        if (mail is null && tel is null) return [];
        return await users.Query()
            .Where(u => u.IsActive
                        && ((mail != null && u.Email != null && u.Email.ToLower() == mail)
                            || (tel != null && u.PhoneE164 == tel)))
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    /// <summary>Whoever started the event and whoever is hosting it.</summary>
    private async Task<IReadOnlyList<Guid>> OrganisersAsync(Campaign campaign, CancellationToken ct)
    {
        var ids = new List<Guid>();
        if (campaign.CreatedByUserId is { } creator && await users.AnyAsync(u => u.Id == creator && u.IsActive, ct))
            ids.Add(creator);
        if (campaign.InviterId is { } inviterId && await inviters.GetByIdAsync(inviterId, ct) is { } inviter)
            ids.AddRange(await AccountsAsync(inviter.Email, inviter.PhoneE164, ct));
        return ids.Distinct().ToList();
    }

    /// <summary>The organisers and the people the event is for: everyone who sees its dashboard.</summary>
    private async Task<IReadOnlyList<Guid>> PeopleOfAsync(Campaign campaign, CancellationToken ct)
    {
        var ids = new List<Guid>(await OrganisersAsync(campaign, ct));
        var list = await celebrants.Query().Where(c => c.CampaignId == campaign.Id).ToListAsync(ct);
        foreach (var c in list) ids.AddRange(await AccountsAsync(c.Email, c.PhoneE164, ct));
        return ids.Distinct().ToList();
    }
}
