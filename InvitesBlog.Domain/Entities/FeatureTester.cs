namespace InvitesBlog.Domain.Entities;

/// <summary>
/// Someone an admin has let try unreleased features. Matched by the email on their account, so they
/// can be added before they ever sign up. Each tester holds only the features switched on for them.
/// </summary>
public sealed class FeatureTester
{
    public Guid Id { get; set; }
    /// <summary>Lowercased.</summary>
    public string Email { get; set; } = default!;
    /// <summary>Feature keys from <c>Features.All</c>.</summary>
    public List<string> Features { get; set; } = new();
    public string? Note { get; set; }
    public Guid? AddedByUserId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A feature an admin has released to everyone. No row means it's still limited to testers.</summary>
public sealed class FeatureRelease
{
    public string Key { get; set; } = default!;
    public DateTimeOffset ReleasedAt { get; set; }
    public Guid? ReleasedByUserId { get; set; }
}

/// <summary>Features that can be tested before release. Add one here and it appears on the Testers page.</summary>
public static class Features
{
    public const string TemplateDesigner = "template-designer";

    public sealed record Definition(string Key, string Name, string Description);

    public static readonly IReadOnlyList<Definition> All =
    [
        new(TemplateDesigner, "Template designer",
            "Build animated templates visually, publish them privately or to the gallery, and open existing templates for editing."),
    ];

    public static bool IsKnown(string key) => All.Any(f => f.Key == key);
}
