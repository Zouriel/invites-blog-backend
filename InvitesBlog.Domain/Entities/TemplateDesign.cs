namespace InvitesBlog.Domain.Entities;

/// <summary>
/// A template being built in the visual designer: the scene, autosaved as the person works. It becomes
/// a real <see cref="Template"/> only when they publish — privately (usable on their own events) or
/// publicly (in the gallery). Publishing again bumps that template's version; the scene stays here so
/// the template is always editable.
///
/// <para>Nothing about publishing trusts the browser: the server compiles <see cref="SceneJson"/> itself,
/// so a design can go live without a human reviewing it. Hand-uploaded HTML keeps its review.</para>
/// </summary>
public sealed class TemplateDesign
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = default!;
    public string SceneJson { get; set; } = default!;

    /// <summary>Bumped on every save. A save naming an older revision is refused, so two open tabs can't silently overwrite each other.</summary>
    public int Revision { get; set; }

    /// <summary>The template this design publishes to — set on first publish, or from the start when it edits an existing template.</summary>
    public Guid? TemplateId { get; set; }

    /// <summary>The revision that was last published; less than <see cref="Revision"/> means unpublished changes.</summary>
    public int? PublishedRevision { get; set; }
    public DateTimeOffset? LastPublishedAt { get; set; }

    /// <summary>The event this design was started for, so its first publish can attach to it.</summary>
    public Guid? CampaignId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One publish of a design. The history the owner sees, and what the public-publish rate limit counts.</summary>
public sealed class TemplateDesignPublish
{
    public Guid Id { get; set; }
    public Guid DesignId { get; set; }
    public Guid UserId { get; set; }
    public Guid TemplateId { get; set; }
    public string Version { get; set; } = default!;
    public string Visibility { get; set; } = default!;
    public int Revision { get; set; }
    public int Bytes { get; set; }
    public DateTimeOffset PublishedAt { get; set; }
}

/// <summary>
/// Somebody flagging a gallery template. Public templates go live without review, so this is the
/// safety net: reports land in an admin queue that can unlist or remove the template.
/// </summary>
public sealed class TemplateReport
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    /// <summary>The signed-in reporter, when there is one. Reporting doesn't require an account.</summary>
    public Guid? ReporterUserId { get; set; }
    /// <summary>offensive | copyright | spam | broken | other.</summary>
    public string Reason { get; set; } = default!;
    public string? Details { get; set; }
    public Enums.TemplateReportStatus Status { get; set; }
    /// <summary>What the admin did: dismissed | unlisted | removed.</summary>
    public string? Resolution { get; set; }
    public string? ResolutionNote { get; set; }
    public Guid? ResolvedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
