namespace InvitesBlog.Domain.Entities;

/// <summary>A published, versioned gallery template (§8.2 Template).</summary>
public sealed class Template
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public string Version { get; set; } = default!;
    public string Category { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string PreviewImageUrl { get; set; } = default!;
    public string? PreviewAnimationUrl { get; set; }
    public bool IsPremium { get; set; }
    public Guid? DesignerInviterId { get; set; }   // community attribution
    public string? DesignerName { get; set; }
    public string SceneJson { get; set; } = default!;
    public string ManifestJson { get; set; } = default!;
    public string PackageUrl { get; set; } = default!;   // compiled package on assets CDN
    public bool IsActive { get; set; } = true;

    /// <summary>"Public" (listed in the gallery) or "Dedicated" (only the assigned requester sees it).</summary>
    public string Visibility { get; set; } = TemplateVisibility.Public;
    /// <summary>Lowercased email the dedicated template is reserved for; null for public templates.</summary>
    public string? AssignedEmail { get; set; }

    /// <summary>Set true on the FIRST use of a <see cref="TemplateVisibility.Dedicated"/> template. A used
    /// dedicated template becomes a read-only gallery showcase — listed but not selectable. Always false
    /// for public templates (they stay freely usable).</summary>
    public bool IsUsed { get; set; }

    /// <summary>The designer account that authored it (§community templates); null for platform templates.</summary>
    public Guid? DesignerUserId { get; set; }

    /// <summary>Lowercased email that commissioned this template, when it came from a paid commission.</summary>
    public string? RequestedByEmail { get; set; }
    /// <summary>The requester's account, when they have one.</summary>
    public Guid? RequestedByUserId { get; set; }

    /// <summary>One-time fee for a bespoke commission. Null for spontaneous public submissions.</summary>
    public decimal? CommissionPrice { get; set; }
    /// <summary>Per-use designer fee charged whenever an inviter starts a campaign from this public
    /// template. Surfaced as its own checkout line item — never folded into the base price.</summary>
    public decimal? UsagePrice { get; set; }

    /// <summary>A commissioned (<see cref="TemplateVisibility.Dedicated"/>) template may only be released
    /// into the public gallery once BOTH the requester and the designer have consented.</summary>
    public bool RequesterConsentToPublish { get; set; }
    public bool DesignerConsentToPublish { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Template visibility modes (§dedicated templates).</summary>
public static class TemplateVisibility
{
    public const string Public = "Public";
    public const string Dedicated = "Dedicated";
}

/// <summary>
/// A designer's template submission moving through the review pipeline (§community templates).
/// A brand-new template and an edit of an already-published one are the same thing — one row walking
/// the <see cref="Enums.CustomTemplateStatus"/> status machine — the edit case simply carries a
/// <see cref="PublishedTemplateId"/>, so an approved edit bumps that template's version instead of
/// creating a new one. Approval is the ONLY moment a real <see cref="Template"/> row is written.
/// </summary>
public sealed class CustomTemplate
{
    public Guid Id { get; set; }
    /// <summary>The designer account that submitted it.</summary>
    public Guid DesignerUserId { get; set; }
    public string Name { get; set; } = default!;
    public string Description { get; set; } = default!;
    public string Category { get; set; } = default!;
    /// <summary>System-generated at submission time — never author-chosen.</summary>
    public string Slug { get; set; } = default!;
    /// <summary>The raw submitted source, kept verbatim for audit and re-review.</summary>
    public string Html { get; set; } = default!;
    /// <summary>Static preview image the designer uploaded with the submission.</summary>
    public string? PreviewImageUrl { get; set; }
    /// <summary>Where the packaged submission was published for review (set once it passes the scan).</summary>
    public string? PackageUrl { get; set; }
    public string ManifestJson { get; set; } = "{}";

    public Enums.CustomTemplateStatus Status { get; set; }
    public string? RejectionReason { get; set; }
    /// <summary>Set on an edit (the template being revised) and on approval of a new submission.</summary>
    public Guid? PublishedTemplateId { get; set; }

    public decimal? CommissionPrice { get; set; }
    public decimal? UsagePrice { get; set; }
    /// <summary>Lowercased email of the person who commissioned this, when it answers a request.</summary>
    public string? RequestedByEmail { get; set; }
    public bool RequesterConsentToPublish { get; set; }
    public bool DesignerConsentToPublish { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
