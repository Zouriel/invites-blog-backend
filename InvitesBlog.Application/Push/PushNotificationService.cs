using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Push;

/// <summary>
/// A notification for one account: short plain words, the app path its tap opens, what kind it is
/// (<see cref="PushKinds"/>), the event or album it groups under on the phone, and the key a newer one
/// replaces it by.
/// </summary>
public sealed record PushNote(Guid UserId, string Title, string Body, string Path,
    string Kind = PushKinds.Invitation, string? Thread = null, string? Collapse = null)
{
    public Dictionary<string, string> Data => PushKinds.Data(Path, Kind, Thread, Collapse);
}

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
    IRepository<MediaBucket> buckets,
    IUnitOfWork uow,
    IPushSender sender,
    Moderation.IModerationViewService? moderation = null,
    IRepository<PortfolioEnquiry>? portfolioEnquiries = null,
    IRepository<Portfolio>? portfolios = null)
{
    /// <summary>Longest stretch of a comment quoted in a notification.</summary>
    public const int QuoteLength = 80;

    public async Task HandleAsync(PushJob job, CancellationToken ct)
    {
        var notes = await NotesAsync(job, ct);
        foreach (var note in notes)
        {
            if (note.UserId == job.ActorUserId) continue;
            // Someone who blocked the actor hears nothing about what they do.
            if (job.ActorUserId is { } actor && moderation is not null
                && await moderation.HasBlockedAsync(note.UserId, actor, ct)) continue;
            await sender.SendAsync(note.UserId, note.Title, note.Body, note.Data, ct);
        }
    }

    /// <summary>What <see cref="HandleAsync"/> would send, one per account, before the actor is left out.</summary>
    public async Task<IReadOnlyList<PushNote>> NotesAsync(PushJob job, CancellationToken ct) => job switch
    {
        InvitationsSent j => await InvitationsAsync(j, ct),
        GuestReplied j => await ReplyAsync(j, ct),
        CommentAdded j => await CommentAsync(j, ct),
        CelebrantAdded j => await CelebrantAsync(j, ct),
        FacesSwitchedOff j => await FacesOffAsync(j, ct),
        PortfolioEnquiryReceived j => await EnquiryAsync(j, ct),
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
                    notes.Add(new PushNote(userId, title, body, $"/invitation/{campaign.Id}",
                        PushKinds.Invitation, campaign.Id.ToString(), $"invite-{campaign.Id:N}"));
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
            // A changed answer replaces the one already on the phone rather than stacking under it.
            .Select(id => new PushNote(id, campaign.Title, body, $"/dashboard/{campaign.Id}",
                PushKinds.Reply, campaign.Id.ToString(), $"reply-{invite.Id:N}"))
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

        var thread = campaign.Id.ToString();
        var notes = new Dictionary<Guid, PushNote>();
        PushNote Replied(Guid to) => new(to, campaign.Title, $"{who} replied: {quote}", path, PushKinds.Comment, thread);
        // Somebody answered your comment: that's the more personal of the two, so it wins. That's whoever
        // wrote the comment they pressed reply on, and whoever started the thread it went into.
        if (job.RepliedToCommentId is { } target && target != comment.ParentId
            && await comments.GetByIdAsync(target, ct) is { DeletedAt: null } answered && answered.CampaignId == comment.CampaignId)
            notes[answered.UserId] = Replied(answered.UserId);
        if (comment.ParentId is { } parentId && await comments.GetByIdAsync(parentId, ct) is { DeletedAt: null } parent)
            notes.TryAdd(parent.UserId, Replied(parent.UserId));

        // The post is the event's, so it is the organisers' post.
        foreach (var id in await OrganisersAsync(campaign, ct))
            notes.TryAdd(id, new PushNote(id, campaign.Title, $"{who} commented: {quote}", path, PushKinds.Comment, thread));
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
                "See who's coming and all the photos.", $"/dashboard/{campaign.Id}",
                PushKinds.Added, campaign.Id.ToString()))
            .ToList();
    }

    // ----- Face grouping switched off -----------------------------------------------------------------

    private async Task<IReadOnlyList<PushNote>> FacesOffAsync(FacesSwitchedOff job, CancellationToken ct)
    {
        var bucket = await buckets.GetByIdAsync(job.BucketId, ct);
        if (bucket is null) return [];
        var album = await AlbumTitleAsync(bucket, ct);
        var body = job.Reason switch
        {
            FacesOffReason.Idle => "Face grouping was switched off and its face data deleted: the album had no new photos for a long time. You can switch it on again.",
            _ => "Face grouping was switched off and its face data deleted because the event's pass or Premium ended.",
        };
        return (await AlbumHostsAsync(bucket, ct))
            .Select(id => new PushNote(id, $"Face grouping is off in {album}", body, $"/buckets/{bucket.Id}",
                PushKinds.Album, bucket.Id.ToString(), $"faces-end-{bucket.Id:N}"))
            .ToList();
    }

    private async Task<IReadOnlyList<PushNote>> EnquiryAsync(PortfolioEnquiryReceived job, CancellationToken ct)
    {
        if (portfolioEnquiries is null || portfolios is null) return [];
        var enquiry = await portfolioEnquiries.GetByIdAsync(job.EnquiryId, ct);
        if (enquiry is null) return [];
        var portfolio = await portfolios.GetByIdAsync(enquiry.PortfolioId, ct);
        if (portfolio is null) return [];
        var body = enquiry.Message.Length > QuoteLength ? enquiry.Message[..QuoteLength].TrimEnd() + "..." : enquiry.Message;
        return [new PushNote(portfolio.UserId, $"New enquiry from {enquiry.Name}", body, "/portfolio/enquiries",
            PushKinds.Enquiry, "portfolio", $"enquiry-{enquiry.Id:N}")];
    }

    /// <summary>What a notification calls an album: its event's title, else its own name.</summary>
    public async Task<string> AlbumTitleAsync(MediaBucket bucket, CancellationToken ct)
    {
        if (bucket.CampaignId != Guid.Empty && await campaigns.GetByIdAsync(bucket.CampaignId, ct) is { } campaign
            && !string.IsNullOrWhiteSpace(campaign.Title))
            return campaign.Title.Trim();
        return string.IsNullOrWhiteSpace(bucket.Name) ? "your album" : bucket.Name.Trim();
    }

    // ----- Who --------------------------------------------------------------------------------------

    /// <summary>Accounts that have proved this email or phone number.</summary>
    public async Task<IReadOnlyList<Guid>> AccountsAsync(string? email, string? phone, CancellationToken ct)
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
    public async Task<IReadOnlyList<Guid>> OrganisersAsync(Campaign campaign, CancellationToken ct)
    {
        var ids = new List<Guid>();
        if (campaign.CreatedByUserId is { } creator && await users.AnyAsync(u => u.Id == creator && u.IsActive, ct))
            ids.Add(creator);
        if (campaign.InviterId is { } inviterId && await inviters.GetByIdAsync(inviterId, ct) is { } inviter)
            ids.AddRange(await AccountsAsync(inviter.Email, inviter.PhoneE164, ct));
        return ids.Distinct().ToList();
    }

    /// <summary>
    /// Who hears about hearts in an album: its owner, and (when it belongs to an event) the event's
    /// organisers and the people it is for.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> AlbumPeopleAsync(MediaBucket bucket, CancellationToken ct)
    {
        var ids = new List<Guid> { bucket.OwnerUserId };
        if (bucket.CampaignId != Guid.Empty && await campaigns.GetByIdAsync(bucket.CampaignId, ct) is { } campaign
            && campaign.Status != CampaignStatus.Cancelled)
            ids.AddRange(await PeopleOfAsync(campaign, ct));
        return ids.Distinct().ToList();
    }

    /// <summary>
    /// Who runs an album: its owner, and (when it belongs to an event) the event's organisers. Only they see an
    /// album's People tab and switch face grouping, so only they hear about it.
    /// </summary>
    public async Task<IReadOnlyList<Guid>> AlbumHostsAsync(MediaBucket bucket, CancellationToken ct)
    {
        var ids = new List<Guid>();
        if (await users.AnyAsync(u => u.Id == bucket.OwnerUserId && u.IsActive, ct)) ids.Add(bucket.OwnerUserId);
        if (bucket.CampaignId != Guid.Empty && await campaigns.GetByIdAsync(bucket.CampaignId, ct) is { } campaign
            && campaign.Status != CampaignStatus.Cancelled)
            ids.AddRange(await OrganisersAsync(campaign, ct));
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
