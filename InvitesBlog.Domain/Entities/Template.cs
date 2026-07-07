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
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A designer's declarative custom template (§6.3).</summary>
public sealed class CustomTemplate
{
    public Guid Id { get; set; }
    public Guid InviterId { get; set; }
    public string Name { get; set; } = default!;
    public string SceneJson { get; set; } = default!;        // declarative design
    public string CompilerVersion { get; set; } = default!;  // compiler version pin
    public Enums.CustomTemplateStatus Status { get; set; }
    public Guid? PublishedTemplateId { get; set; }           // gallery template once approved
    public string? Category { get; set; }
    public bool AnonymousAttribution { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
