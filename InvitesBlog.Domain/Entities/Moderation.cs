namespace InvitesBlog.Domain.Entities;

/// <summary>
/// Somebody flagging something another person put on invites.blog: an event's post, a comment, a
/// photo or video in an album, or a person (their name or picture). App Store guideline 1.2 and Google
/// Play's user-generated content policy both ask for this. Gallery templates have their own,
/// older queue (<see cref="TemplateReport"/>).
///
/// <para>The reporter stops seeing the item at once, whatever happens to the report: their own
/// report is the hide list. An admin then dismisses the report, or removes the item for everyone,
/// and may suspend the account behind it.</para>
/// </summary>
public sealed class ContentReport
{
    public Guid Id { get; set; }

    /// <summary>
    /// The signed-in account that reported it. Null only for a visitor's "Ask to remove a photo" on a Studio portfolio
    /// (no account needed there: the person in the photo may have none); those are rate limited by address instead.
    /// </summary>
    public Guid? ReporterUserId { get; set; }

    /// <summary>post | comment | photo | user. See <c>ContentReportKinds</c>.</summary>
    public string Kind { get; set; } = default!;

    /// <summary>The post's event id, the comment's id, the photo's id, or the person's account id.</summary>
    public Guid TargetId { get; set; }

    /// <summary>The event it was seen in, when there is one.</summary>
    public Guid? CampaignId { get; set; }

    /// <summary>Whose it is, when known: the account an admin may suspend.</summary>
    public Guid? SubjectUserId { get; set; }

    /// <summary>abusive | hateful | sexual | violent | spam | other.</summary>
    public string Reason { get; set; } = default!;

    public string? Note { get; set; }

    /// <summary>What the item said or showed when it was reported, so the queue still makes sense after an edit.</summary>
    public string? Snapshot { get; set; }

    public Enums.TemplateReportStatus Status { get; set; }

    /// <summary>dismissed | removed.</summary>
    public string? Resolution { get; set; }
    public string? ResolutionNote { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}

/// <summary>
/// One account choosing not to see another. One direction only: the person blocked isn't told and
/// still sees everything they saw before. Their posts, comments, photos and videos disappear for the
/// blocker, and nothing they do sends the blocker a notification. Guest lists and replies are not
/// touched, so nobody can break an event they organise by blocking (or being blocked by) a guest.
/// </summary>
public sealed class UserBlock
{
    public Guid Id { get; set; }
    public Guid BlockerUserId { get; set; }
    public Guid BlockedUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
