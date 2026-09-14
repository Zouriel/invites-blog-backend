namespace InvitesBlog.Application.Dtos.Feed;

/// <summary>One picture in a post's header. A video is shown by its still.</summary>
public sealed record FeedImageDto(string Url, bool IsVideo);

/// <summary>An event as a post in someone's feed.</summary>
/// <param name="Caption">The organiser's words, or text taken from the invitation when they haven't written any.</param>
/// <param name="CaptionIsAuto">True when the caption came from the invitation rather than the organiser.</param>
/// <param name="ImagesAreCover">True when the header is the invitation's cover because there are no photos to show this viewer.</param>
/// <param name="Role">host, manager, celebrant or guest: how this viewer is involved.</param>
/// <param name="Link">Where "Open" goes for this viewer: the event's dashboard, or their invitation.</param>
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
    DateTimeOffset LastActivityAt);

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
    IReadOnlyList<FeedCommentDto> Replies);

public sealed record AddFeedCommentRequest(string Body, Guid? ParentId = null);

public sealed record SetLikeRequest(bool Liked);

public sealed record LikeStateDto(int LikeCount, bool LikedByMe);

/// <summary>An empty caption goes back to the text taken from the invitation.</summary>
public sealed record SetCaptionRequest(string? Caption);
