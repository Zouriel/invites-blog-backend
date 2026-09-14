namespace InvitesBlog.Domain.Entities;

/// <summary>
/// A comment on an event's post in the feed. One level of replies: a reply points at a top-level
/// comment through <see cref="ParentId"/>, never at another reply.
/// </summary>
public sealed class PostComment
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }

    /// <summary>The account that wrote it. Everyone in the feed is signed in.</summary>
    public Guid UserId { get; set; }

    public Guid? ParentId { get; set; }
    public string Body { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Removed by its author or by the event's organiser. Kept so replies can say so.</summary>
    public DateTimeOffset? DeletedAt { get; set; }
}

/// <summary>A like on an event's post. One per account per event.</summary>
public sealed class PostLike
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A like on a comment. One per account per comment.</summary>
public sealed class CommentLike
{
    public Guid Id { get; set; }
    public Guid CommentId { get; set; }
    public Guid UserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
