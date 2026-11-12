namespace InvitesBlog.Application.Dtos.Feed;

/// <summary>One picture in a post's header. A video is shown by its still.</summary>
public sealed record FeedImageDto(string Url, bool IsVideo);

/// <summary>An event as a post in someone's feed.</summary>
/// <param name="Caption">The organiser's words, or text taken from the invitation when they haven't written any.</param>
/// <param name="CaptionIsAuto">True when the caption came from the invitation rather than the organiser.</param>
/// <param name="ImagesAreCover">True when the header is the invitation's cover because there are no photos to show this viewer.</param>
/// <param name="Role">host, manager, celebrant or guest: how this viewer is involved.</param>
/// <param name="Link">Where "Open" goes for this viewer: the event's dashboard, or their invitation.</param>
/// <param name="Venue">The place as the invitation words it (free text). Not the venue on the Venue plan: that is VenueName.</param>
public sealed record FeedPostDto(
    Guid CampaignId,
    string Title,
    string? HostName,
    DateTimeOffset EventStartAt,
    string? Venue,
    string? Caption,
    bool CaptionIsAuto,
    IReadOnlyList<FeedImageDto> Images,
    bool ImagesAreCover,
    int PhotoCount,
    int LikeCount,
    bool LikedByMe,
    int CommentCount,
    string Role,
    bool CanModerate,
    string Link,
    DateTimeOffset LastActivityAt,
    string? HostAvatarUrl = null,
    /// <summary>The account that made the event, for Report and Block. Null for very old events.</summary>
    Guid? HostUserId = null,
    /// <summary>The event's IANA time zone: EventStartAt (an instant) is shown on this clock.</summary>
    string TimeZone = InvitesBlog.Domain.Events.EventZones.Legacy,
    /// <summary>The venue (on the Venue plan) the event is held at, for the badge at the post's top corner; null when none.</summary>
    string? VenueName = null,
    /// <summary>That venue's logo; null when it has none (show its first letter) or there is no venue.</summary>
    string? VenueLogoUrl = null);

public sealed record FeedPageDto(IReadOnlyList<FeedPostDto> Items, bool HasMore);

public sealed record FeedCommentDto(
    Guid Id,
    Guid? ParentId,
    string AuthorName,
    bool AuthorIsHost,
    string Body,
    DateTimeOffset CreatedAt,
    int LikeCount,
    bool LikedByMe,
    bool CanDelete,
    IReadOnlyList<FeedCommentDto> Replies,
    string? AuthorAvatarUrl = null,
    /// <summary>The account that wrote it, for Report and Block.</summary>
    Guid? AuthorId = null);

public sealed record AddFeedCommentRequest(string Body, Guid? ParentId = null);

public sealed record SetLikeRequest(bool Liked);

public sealed record LikeStateDto(int LikeCount, bool LikedByMe);

/// <summary>The photos picked to head an event's post, from its default bucket.</summary>
/// <param name="BucketId">The default bucket they come from; null when the event has no bucket yet.</param>
/// <param name="PhotoIds">In the order they show. Empty means the bucket's first photos.</param>
public sealed record FeedCoversDto(Guid? BucketId, IReadOnlyList<Guid> PhotoIds, int Max);

public sealed record SetFeedCoversRequest(IReadOnlyList<Guid> PhotoIds);

/// <summary>An empty caption goes back to the text taken from the invitation.</summary>
public sealed record SetCaptionRequest(string? Caption);
