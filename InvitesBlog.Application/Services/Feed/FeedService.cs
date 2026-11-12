using InvitesBlog.Application.Push;
using System.Text.Json;
using System.Text.Json.Nodes;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.Application.Dtos.Feed;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.MediaBuckets;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Moderation;

namespace InvitesBlog.Application.Services.Feed;

/// <summary>
/// The home feed: every event someone is involved in, as a post with the event's first photos, the
/// organiser's caption, likes and comments. Newest activity first.
///
/// <para><b>Who sees a post.</b> The organiser and the people the event is for, from the moment the
/// event exists. A guest once their own invitation has actually gone out to them. Nobody else, and
/// never a cancelled event.</para>
/// </summary>
public interface IFeedService
{
    Task<FeedPageDto> FeedAsync(int skip, int take, CancellationToken ct = default);
    Task<IReadOnlyList<FeedCommentDto>> CommentsAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>
    /// One event's post, as the feed shows it: what a tapped comment notification opens. Not found when the
    /// person can't see it (not involved, a save the date, or taken out of the feed for them).
    /// </summary>
    Task<FeedPostDto> PostAsync(Guid campaignId, CancellationToken ct = default);
    Task<FeedCommentDto> AddCommentAsync(Guid campaignId, AddFeedCommentRequest req, CancellationToken ct = default);
    Task DeleteCommentAsync(Guid campaignId, Guid commentId, CancellationToken ct = default);
    Task<LikeStateDto> SetPostLikeAsync(Guid campaignId, SetLikeRequest req, CancellationToken ct = default);
    Task<LikeStateDto> SetCommentLikeAsync(Guid campaignId, Guid commentId, SetLikeRequest req, CancellationToken ct = default);
    Task<FeedPostDto> SetCaptionAsync(Guid campaignId, SetCaptionRequest req, CancellationToken ct = default);
    Task<FeedCoversDto> CoversAsync(Guid campaignId, CancellationToken ct = default);
    Task<FeedCoversDto> SetCoversAsync(Guid campaignId, SetFeedCoversRequest req, CancellationToken ct = default);

    /// <summary>Whether the signed-in account has this event in its feed (so may report what's in it).</summary>
    Task<bool> CanSeeAsync(Guid campaignId, CancellationToken ct = default);
}

public enum FeedRole
{
    Guest,
    Celebrant,
    Manager,
    Host,
}

public sealed class FeedService(
    ICurrentUser currentUser,
    IRepository<AppUser> users,
    ICampaignRepository campaigns,
    IInviterRepository inviters,
    IRepository<CampaignCelebrant> celebrants,
    IGuestRepository guests,
    IInviteRepository invites,
    IRepository<EventPhoto> photos,
    IRepository<MediaBucket> buckets,
    ITemplateRepository templates,
    IRepository<PostComment> comments,
    IRepository<PostLike> postLikes,
    IRepository<CommentLike> commentLikes,
    IMediaBucketService bucketService,
    IUnitOfWork uow,
    IPushQueue push,
    IModerationViewService? moderation = null,
    IRepository<Venue>? venues = null) : IFeedService
{
    /// <summary>How many photos a post's header shows.</summary>
    public const int HeaderImages = 6;

    public const int MaxCommentLength = 1000;
    public const int MaxCaptionLength = 2000;

    public async Task<FeedPageDto> FeedAsync(int skip, int take, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        take = Math.Clamp(take, 1, 20);
        skip = Math.Max(0, skip);

        var involved = await InvolvedAsync(me, ct);
        if (involved.Count == 0) return new FeedPageDto([], false);

        var ids = involved.Keys.ToList();
        var events = await campaigns.Query()
            // A save the date is not an event post: it has no photos and the invitation follows it.
            .Where(c => ids.Contains(c.Id) && c.Status != CampaignStatus.Cancelled && c.Kind != CampaignKind.SaveTheDate)
            .ToListAsync(ct);
        // Taken out of the feed after a report, or hidden by this viewer (reported, or from someone
        // they blocked). An event's organisers always still see their own post.
        var view = await ViewAsync(ct);
        events = events
            .Where(c => involved[c.Id] >= FeedRole.Manager
                        || (c.PostHiddenAt is null && !view.HidesPost(c.Id, c.CreatedByUserId)))
            .ToList();
        if (events.Count == 0) return new FeedPageDto([], false);

        // Newest activity first: the event being made, a photo added, or someone commenting.
        var liveIds = events.Select(c => c.Id).ToList();
        var lastPhoto = (await photos.Query()
                .Where(p => p.CampaignId != null && liveIds.Contains(p.CampaignId.Value) && p.DeletedAt == null)
                .Select(p => new { CampaignId = p.CampaignId!.Value, p.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(p => p.CampaignId)
            .ToDictionary(g => g.Key, g => g.Max(p => p.CreatedAt));
        var lastComment = (await comments.Query()
                .Where(c => liveIds.Contains(c.CampaignId) && c.DeletedAt == null)
                .Select(c => new { c.CampaignId, c.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(c => c.CampaignId)
            .ToDictionary(g => g.Key, g => g.Max(c => c.CreatedAt));

        DateTimeOffset Activity(Campaign c)
        {
            var at = c.CreatedAt;
            if (lastPhoto.TryGetValue(c.Id, out var p) && p > at) at = p;
            if (lastComment.TryGetValue(c.Id, out var m) && m > at) at = m;
            return at;
        }

        var ordered = events
            .Select(c => (Campaign: c, Activity: Activity(c)))
            .OrderByDescending(x => x.Activity)
            .ToList();
        var page = ordered.Skip(skip).Take(take).ToList();

        // The venues of the page's events, for their badges: one query for the page.
        var faces = await VenueFacesAsync(page.Select(x => x.Campaign), ct);
        var posts = new List<FeedPostDto>(page.Count);
        foreach (var (campaign, activity) in page)
            posts.Add(await DescribeAsync(me, campaign, involved[campaign.Id], activity, ct, view, faces));

        return new FeedPageDto(posts, skip + page.Count < ordered.Count);
    }

    public async Task<FeedPostDto> PostAsync(Guid campaignId, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        var involved = await InvolvedAsync(me, ct);
        var campaign = involved.TryGetValue(campaignId, out var role) ? await campaigns.GetByIdAsync(campaignId, ct) : null;
        // The same rules as the feed itself.
        var view = await ViewAsync(ct);
        if (campaign is null || campaign.Status == CampaignStatus.Cancelled || campaign.Kind == CampaignKind.SaveTheDate
            || (role < FeedRole.Manager && (campaign.PostHiddenAt is not null || view.HidesPost(campaign.Id, campaign.CreatedByUserId))))
            throw new NotFoundException("That post isn't in your feed.");

        var activity = campaign.CreatedAt;
        var lastPhoto = await photos.Query().Where(p => p.CampaignId == campaignId && p.DeletedAt == null)
            .MaxAsync(p => (DateTimeOffset?)p.CreatedAt, ct);
        var lastComment = await comments.Query().Where(c => c.CampaignId == campaignId && c.DeletedAt == null)
            .MaxAsync(c => (DateTimeOffset?)c.CreatedAt, ct);
        if (lastPhoto > activity) activity = lastPhoto.Value;
        if (lastComment > activity) activity = lastComment.Value;
        return await DescribeAsync(me, campaign, role, activity, ct, view);
    }

    public async Task<IReadOnlyList<FeedCommentDto>> CommentsAsync(Guid campaignId, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        var (campaign, role) = await RequireInvolvedAsync(me, campaignId, ct);

        var view = await ViewAsync(ct);
        var all = (await comments.Query()
                .Where(c => c.CampaignId == campaignId)
                .OrderBy(c => c.CreatedAt)
                .ToListAsync(ct))
            .Where(c => !view.HidesComment(c))
            .ToList();
        return await DescribeCommentsAsync(me, campaign, role, all, ct);
    }

    public async Task<FeedCommentDto> AddCommentAsync(
        Guid campaignId, AddFeedCommentRequest req, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        var (campaign, role) = await RequireInvolvedAsync(me, campaignId, ct);

        var body = (req.Body ?? string.Empty).Trim();
        if (body.Length == 0)
            throw new BusinessRuleException("Write something first.", "comment_empty");
        if (body.Length > MaxCommentLength)
            throw new BusinessRuleException($"Keep a comment under {MaxCommentLength} characters.", "comment_too_long");
        ObjectionableText.Check(body);

        // The comment they pressed reply on, before replies are folded into its thread below.
        var repliedTo = req.ParentId;
        if (req.ParentId is { } parentId)
        {
            var parent = await comments.Query()
                .FirstOrDefaultAsync(c => c.Id == parentId && c.CampaignId == campaignId, ct)
                ?? throw new NotFoundException("That comment is no longer here.");
            if (parent.DeletedAt is not null)
                throw new BusinessRuleException("That comment was removed, so it can't be replied to.", "comment_removed");
            // One level of replies: answering a reply answers the comment it belongs to.
            if (parent.ParentId is { } grandparent) req = req with { ParentId = grandparent };
        }

        var comment = new PostComment
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            UserId = me.Id,
            ParentId = req.ParentId,
            Body = body,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await comments.AddAsync(comment, ct);
        await uow.SaveChangesAsync(ct);
        push.Enqueue(new CommentAdded(comment.Id, me.Id, repliedTo));

        return (await DescribeCommentsAsync(me, campaign, role, [comment], ct))[0];
    }

    public async Task DeleteCommentAsync(Guid campaignId, Guid commentId, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        var (_, role) = await RequireInvolvedAsync(me, campaignId, ct);

        var comment = await comments.Query(tracking: true)
            .FirstOrDefaultAsync(c => c.Id == commentId && c.CampaignId == campaignId, ct)
            ?? throw new NotFoundException("That comment is no longer here.");
        if (comment.DeletedAt is not null) return;

        if (comment.UserId != me.Id && role < FeedRole.Manager)
            throw new ForbiddenException("Only its author or the event's organiser can remove a comment.");

        var now = DateTimeOffset.UtcNow;
        comment.DeletedAt = now;
        comments.Update(comment);

        // Its replies go with it; they answered something that is no longer there.
        var replies = await comments.Query(tracking: true)
            .Where(c => c.ParentId == commentId && c.DeletedAt == null)
            .ToListAsync(ct);
        foreach (var reply in replies)
        {
            reply.DeletedAt = now;
            comments.Update(reply);
        }

        await uow.SaveChangesAsync(ct);
    }

    public async Task<LikeStateDto> SetPostLikeAsync(Guid campaignId, SetLikeRequest req, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        await RequireInvolvedAsync(me, campaignId, ct);

        var existing = await postLikes.Query(tracking: true)
            .FirstOrDefaultAsync(l => l.CampaignId == campaignId && l.UserId == me.Id, ct);
        if (req.Liked && existing is null)
            await postLikes.AddAsync(new PostLike
            {
                Id = Guid.NewGuid(), CampaignId = campaignId, UserId = me.Id, CreatedAt = DateTimeOffset.UtcNow,
            }, ct);
        else if (!req.Liked && existing is not null)
            postLikes.Remove(existing);
        await uow.SaveChangesAsync(ct);

        var count = await postLikes.Query().CountAsync(l => l.CampaignId == campaignId, ct);
        return new LikeStateDto(count, req.Liked);
    }

    public async Task<LikeStateDto> SetCommentLikeAsync(
        Guid campaignId, Guid commentId, SetLikeRequest req, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        await RequireInvolvedAsync(me, campaignId, ct);

        var comment = await comments.Query()
            .FirstOrDefaultAsync(c => c.Id == commentId && c.CampaignId == campaignId && c.DeletedAt == null, ct)
            ?? throw new NotFoundException("That comment is no longer here.");

        var existing = await commentLikes.Query(tracking: true)
            .FirstOrDefaultAsync(l => l.CommentId == comment.Id && l.UserId == me.Id, ct);
        if (req.Liked && existing is null)
            await commentLikes.AddAsync(new CommentLike
            {
                Id = Guid.NewGuid(), CommentId = comment.Id, UserId = me.Id, CreatedAt = DateTimeOffset.UtcNow,
            }, ct);
        else if (!req.Liked && existing is not null)
            commentLikes.Remove(existing);
        await uow.SaveChangesAsync(ct);

        var count = await commentLikes.Query().CountAsync(l => l.CommentId == comment.Id, ct);
        return new LikeStateDto(count, req.Liked);
    }

    public async Task<FeedPostDto> SetCaptionAsync(Guid campaignId, SetCaptionRequest req, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        var (campaign, role) = await RequireInvolvedAsync(me, campaignId, ct);
        if (role < FeedRole.Manager)
            throw new ForbiddenException("Only the event's organiser can change its post.");

        var caption = req.Caption?.Trim();
        if (caption is { Length: > MaxCaptionLength })
            throw new BusinessRuleException($"Keep the post under {MaxCaptionLength} characters.", "caption_too_long");
        ObjectionableText.Check(caption);

        var tracked = await campaigns.GetByIdAsync(campaignId, ct) ?? campaign;
        tracked.PostCaption = string.IsNullOrWhiteSpace(caption) ? null : caption;
        campaigns.Update(tracked);
        await uow.SaveChangesAsync(ct);

        return await DescribeAsync(me, tracked, role, DateTimeOffset.UtcNow, ct, await ViewAsync(ct));
    }

    public async Task<FeedCoversDto> CoversAsync(Guid campaignId, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        var (campaign, role) = await RequireInvolvedAsync(me, campaignId, ct);
        if (role < FeedRole.Manager)
            throw new ForbiddenException("Only the event's organiser can choose its cover photos.");

        var bucketId = await DefaultBucketAsync(campaignId, ct);
        var ids = bucketId is { } b ? await LivePicksAsync(campaign.PostCoverPhotoIds, b, ct) : [];
        return new FeedCoversDto(bucketId, ids, HeaderImages);
    }

    public async Task<FeedCoversDto> SetCoversAsync(Guid campaignId, SetFeedCoversRequest req, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        var (_, role) = await RequireInvolvedAsync(me, campaignId, ct);
        if (role < FeedRole.Manager)
            throw new ForbiddenException("Only the event's organiser can choose its cover photos.");

        var wanted = (req.PhotoIds ?? []).Distinct().ToList();
        if (wanted.Count > HeaderImages)
            throw new BusinessRuleException($"Pick up to {HeaderImages} cover photos.", "too_many_covers");

        var bucketId = await DefaultBucketAsync(campaignId, ct);
        if (wanted.Count > 0)
        {
            if (bucketId is not { } b)
                throw new BusinessRuleException("This event has no photos to choose from yet.", "no_bucket");
            var live = await LivePicksAsync(wanted, b, ct);
            if (live.Count != wanted.Count)
                throw new BusinessRuleException("Some of those photos aren't in this event's main album any more.", "cover_not_in_bucket");
        }

        var tracked = await campaigns.GetByIdAsync(campaignId, ct)
                      ?? throw new NotFoundException("That event no longer exists.");
        tracked.PostCoverPhotoIds = wanted;
        campaigns.Update(tracked);
        await uow.SaveChangesAsync(ct);
        return new FeedCoversDto(bucketId, wanted, HeaderImages);
    }

    /// <summary>The event's default bucket: the oldest one, which the invitation's camera posts to.</summary>
    private Task<Guid?> DefaultBucketAsync(Guid campaignId, CancellationToken ct) =>
        buckets.Query()
            .Where(b => b.CampaignId == campaignId)
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .Select(b => (Guid?)b.Id)
            .FirstOrDefaultAsync(ct);

    /// <summary>Those of the picked photos still in the bucket, in the order they were picked.</summary>
    private async Task<List<Guid>> LivePicksAsync(IReadOnlyCollection<Guid> picks, Guid bucketId, CancellationToken ct)
    {
        if (picks.Count == 0) return [];
        var found = await photos.Query()
            .Where(p => p.BucketId == bucketId && p.DeletedAt == null && picks.Contains(p.Id))
            .Select(p => p.Id)
            .ToListAsync(ct);
        return picks.Where(found.Contains).ToList();
    }

    // ---------- who is involved ----------

    /// <summary>Every event this account is involved in, with the closest way it is involved.</summary>
    private async Task<Dictionary<Guid, FeedRole>> InvolvedAsync(AppUser me, CancellationToken ct)
    {
        var result = new Dictionary<Guid, FeedRole>();
        void Offer(Guid id, FeedRole role)
        {
            if (!result.TryGetValue(id, out var had) || role > had) result[id] = role;
        }

        var (email, phone) = Contacts(me);

        var inviterIds = email is null && phone is null
            ? []
            : await inviters.Query()
                .Where(i => (email != null && i.Email == email) || (phone != null && i.PhoneE164 == phone))
                .Select(i => i.Id)
                .ToListAsync(ct);
        foreach (var id in await campaigns.Query()
                     .Where(c => c.CreatedByUserId == me.Id || (c.InviterId != null && inviterIds.Contains(c.InviterId.Value)))
                     .Select(c => c.Id)
                     .ToListAsync(ct))
            Offer(id, FeedRole.Host);

        if (email is null && phone is null) return result;

        foreach (var c in await celebrants.Query()
                     .Where(c => (email != null && c.Email == email) || (phone != null && c.PhoneE164 == phone))
                     .Select(c => new { c.CampaignId, c.CanManage })
                     .ToListAsync(ct))
            Offer(c.CampaignId, c.CanManage ? FeedRole.Manager : FeedRole.Celebrant);

        var guestRows = await guests.Query()
            .Where(g => !g.OptedOut && ((email != null && g.Email == email) || (phone != null && g.PhoneE164 == phone)))
            .Select(g => new { g.Id, g.CampaignId })
            .ToListAsync(ct);
        if (guestRows.Count > 0)
        {
            var guestIds = guestRows.Select(g => g.Id).ToList();
            var guestCampaignIds = guestRows.Select(g => g.CampaignId).Distinct().ToList();

            // On the list of an event the host has finished; see GuestVisibility.
            foreach (var id in await campaigns.Query()
                         .Where(c => guestCampaignIds.Contains(c.Id) && !GuestVisibility.Hidden.Contains(c.Status))
                         .Select(c => c.Id)
                         .ToListAsync(ct))
                Offer(id, FeedRole.Guest);

            // An invitation that really went out counts whatever state the event is in.
            foreach (var id in await invites.Query()
                         .Where(i => guestIds.Contains(i.GuestId)
                                     && (i.Status == InviteStatus.Sent || i.Status == InviteStatus.Viewed))
                         .Select(i => i.CampaignId)
                         .Distinct()
                         .ToListAsync(ct))
                Offer(id, FeedRole.Guest);
        }

        return result;
    }

    private async Task<(Campaign Campaign, FeedRole Role)> RequireInvolvedAsync(
        AppUser me, Guid campaignId, CancellationToken ct)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");
        if (campaign.Status == CampaignStatus.Cancelled)
            throw new BusinessRuleException("This event was cancelled.", "event_cancelled");

        var involved = await InvolvedAsync(me, ct);
        if (!involved.TryGetValue(campaignId, out var role))
            throw new ForbiddenException("This event isn't in your feed.");
        return (campaign, role);
    }

    // ---------- describing ----------

    /// <summary>The name and logo of the venues these events are held at, by venue id.</summary>
    private async Task<IReadOnlyDictionary<Guid, (string Name, string? LogoUrl)>> VenueFacesAsync(
        IEnumerable<Campaign> events, CancellationToken ct)
    {
        var ids = events.Select(c => c.VenueId).OfType<Guid>().Distinct().ToList();
        if (ids.Count == 0 || venues is null) return new Dictionary<Guid, (string, string?)>();
        return (await venues.Query().Where(v => ids.Contains(v.Id)).Select(v => new { v.Id, v.Name, v.LogoUrl }).ToListAsync(ct))
            .ToDictionary(v => v.Id, v => (v.Name, v.LogoUrl));
    }

    private async Task<FeedPostDto> DescribeAsync(
        AppUser me, Campaign campaign, FeedRole role, DateTimeOffset activity, CancellationToken ct, ModerationView view,
        IReadOnlyDictionary<Guid, (string Name, string? LogoUrl)>? venueFaces = null)
    {
        venueFaces ??= await VenueFacesAsync([campaign], ct);
        (string Name, string? LogoUrl)? atVenue = campaign.VenueId is { } venueId && venueFaces.TryGetValue(venueId, out var face) ? face : null;
        var defaultBucket = await DefaultBucketAsync(campaign.Id, ct);

        // The event's first photos, from the bucket the invitation's camera posts to, but only for
        // someone allowed to look into it. Everyone else sees the invitation's cover.
        var images = new List<FeedImageDto>();
        if (defaultBucket is { } bucketId && (role > FeedRole.Guest || await bucketService.MayViewAsync(bucketId, ct)))
        {
            // The organiser's picks first, in their order; the bucket's first photos when there are none.
            var picks = campaign.PostCoverPhotoIds ?? [];
            var chosen = picks.Count == 0
                ? []
                : (await photos.Query()
                        .Where(p => p.BucketId == bucketId && p.DeletedAt == null && picks.Contains(p.Id))
                        .ToListAsync(ct))
                    .Where(p => !view.HidesPhoto(p))
                    .OrderBy(p => picks.IndexOf(p.Id))
                    .ToList();
            // A few spare, so photos this viewer hid don't leave the header short.
            var shown = chosen.Count > 0
                ? chosen
                : (await photos.Query()
                        .Where(p => p.BucketId == bucketId && p.DeletedAt == null)
                        .OrderBy(p => p.CreatedAt)
                        .Take(view.IsEmpty ? HeaderImages : HeaderImages * 5)
                        .ToListAsync(ct))
                    .Where(p => !view.HidesPhoto(p))
                    .Take(HeaderImages)
                    .ToList();
            images = shown
                .Select(p => p.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase)
                    ? new FeedImageDto(p.ThumbUrl, true)
                    : new FeedImageDto(p.Url, false))
                .ToList();
        }

        var cover = images.Count == 0;
        if (cover)
        {
            // Older templates point their preview at the design's page, which is not a picture.
            var url = CampaignCover.Read(campaign.CustomContentJson)
                      ?? TemplatePoster.OrNull((await templates.GetByIdAsync(campaign.TemplateId, ct))?.PreviewImageUrl);
            if (!string.IsNullOrWhiteSpace(url))
                images.Add(new FeedImageDto(url, false));
        }

        var photoCount = await photos.Query()
            .CountAsync(p => p.CampaignId == campaign.Id && p.DeletedAt == null, ct);
        var likeCount = await postLikes.Query().CountAsync(l => l.CampaignId == campaign.Id, ct);
        var likedByMe = await postLikes.Query().AnyAsync(l => l.CampaignId == campaign.Id && l.UserId == me.Id, ct);
        var commentCount = view.IsEmpty
            ? await comments.Query().CountAsync(c => c.CampaignId == campaign.Id && c.DeletedAt == null, ct)
            : (await comments.Query()
                    .Where(c => c.CampaignId == campaign.Id && c.DeletedAt == null)
                    .ToListAsync(ct))
                .Count(c => !view.HidesComment(c));

        string? hostName = null;
        if (campaign.InviterId is { } inviterId)
            hostName = (await inviters.GetByIdAsync(inviterId, ct))?.Name;
        var hostUser = campaign.CreatedByUserId is { } creator ? await users.GetByIdAsync(creator, ct) : null;
        hostName ??= hostUser?.DisplayName;

        var (autoCaption, venue) = ReadContent(campaign.CustomContentJson);

        return new FeedPostDto(
            campaign.Id,
            campaign.Title,
            hostName,
            campaign.EventStartAt,
            venue,
            campaign.PostCaption ?? autoCaption,
            campaign.PostCaption is null,
            images,
            cover,
            photoCount,
            likeCount,
            likedByMe,
            commentCount,
            role.ToString().ToLowerInvariant(),
            role >= FeedRole.Manager,
            role == FeedRole.Guest ? $"/invitation/{campaign.Id}" : $"/dashboard/{campaign.Id}",
            activity,
            hostUser?.AvatarUrl,
            campaign.CreatedByUserId,
            campaign.TimeZone,
            atVenue?.Name,
            atVenue?.LogoUrl);
    }

    private async Task<List<FeedCommentDto>> DescribeCommentsAsync(
        AppUser me, Campaign campaign, FeedRole role, IReadOnlyList<PostComment> all, CancellationToken ct)
    {
        var live = all.Where(c => c.DeletedAt == null).ToList();
        if (live.Count == 0) return [];

        var authorIds = live.Select(c => c.UserId).Distinct().ToList();
        var authors = await users.Query()
            .Where(u => authorIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.AvatarUrl })
            .ToDictionaryAsync(u => u.Id, ct);

        var ids = live.Select(c => c.Id).ToList();
        var likes = await commentLikes.Query()
            .Where(l => ids.Contains(l.CommentId))
            .Select(l => new { l.CommentId, l.UserId })
            .ToListAsync(ct);

        FeedCommentDto Describe(PostComment c, IReadOnlyList<FeedCommentDto> replies) => new(
            c.Id,
            c.ParentId,
            authors.GetValueOrDefault(c.UserId)?.DisplayName ?? "Someone",
            c.UserId == campaign.CreatedByUserId,
            c.Body,
            c.CreatedAt,
            likes.Count(l => l.CommentId == c.Id),
            likes.Any(l => l.CommentId == c.Id && l.UserId == me.Id),
            c.UserId == me.Id || role >= FeedRole.Manager,
            replies,
            authors.GetValueOrDefault(c.UserId)?.AvatarUrl,
            c.UserId);

        // A single new reply is described on its own; a full list nests replies under their comment.
        if (live.Count == 1 && live[0].ParentId is not null)
            return [Describe(live[0], [])];

        var topLevel = live.Where(c => c.ParentId == null).ToList();
        return topLevel
            .Select(c => Describe(c, live
                .Where(r => r.ParentId == c.Id)
                .Select(r => Describe(r, []))
                .ToList()))
            .ToList();
    }

    /// <summary>A line of the invitation's own wording, and its venue, for a post nobody has written.</summary>
    public static (string? Caption, string? Venue) ReadContent(string? customContentJson)
    {
        if (string.IsNullOrWhiteSpace(customContentJson)) return (null, null);
        try
        {
            if (JsonNode.Parse(customContentJson) is not JsonObject content) return (null, null);
            string? Text(string key) => content[key] is JsonValue v && v.TryGetValue<string>(out var s)
                ? (string.IsNullOrWhiteSpace(s) ? null : s.Trim())
                : null;
            return (Text("description") ?? Text("subtitle"), Text("venueName"));
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    public async Task<bool> CanSeeAsync(Guid campaignId, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        var campaign = await campaigns.GetByIdAsync(campaignId, ct);
        if (campaign is null) return false;
        return (await InvolvedAsync(me, ct)).ContainsKey(campaignId);
    }

    private Task<ModerationView> ViewAsync(CancellationToken ct) =>
        moderation is null ? Task.FromResult(ModerationView.Empty) : moderation.ForCurrentAsync(ct);

    private async Task<AppUser> MeAsync(CancellationToken ct)
    {
        var id = currentUser.UserId ?? throw new UnauthorizedException();
        return await users.GetByIdAsync(id, ct) ?? throw new UnauthorizedException();
    }

    private static (string? Email, string? Phone) Contacts(AppUser me) =>
        (string.IsNullOrWhiteSpace(me.Email) ? null : me.Email.Trim().ToLowerInvariant(),
         string.IsNullOrWhiteSpace(me.PhoneE164) ? null : me.PhoneE164.Trim());
}
