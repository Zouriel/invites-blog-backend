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

    /// <summary>
    /// Set when an admin takes a template out of the gallery after a report. The owner keeps using it
    /// privately, but can't list it again — only an admin can.
    /// </summary>
    public DateTimeOffset? UnlistedByAdminAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Template visibility modes (§dedicated templates).</summary>
public static class TemplateVisibility
{
    public const string Public = "Public";
    public const string Dedicated = "Dedicated";

    /// <summary>
    /// A design the customer brought themselves. One row per campaign, holding that campaign's
    /// uploaded package.
    ///
    /// <para><b>Why a visibility rather than a flag.</b> Every gallery read already asks for
    /// <see cref="Public"/>, or <see cref="Dedicated"/> once used — so a third value is invisible to
    /// all of them without a single query being touched. "Never showcased" is then a property of the
    /// data rather than a rule each new listing has to remember.</para>
    /// </summary>
    public const string Imported = "Imported";

    /// <summary>
    /// Built in the designer and published for the owner's own events only. Like
    /// <see cref="Imported"/> it is invisible to every gallery read, which ask for Public or Dedicated;
    /// unlike it, the owner (<see cref="Template.DesignerUserId"/>) can start any number of events
    /// from it, and nobody else can start one at all.
    /// </summary>
    public const string Private = "Private";
}
