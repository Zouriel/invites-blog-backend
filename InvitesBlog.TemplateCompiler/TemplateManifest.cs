using System.Text.Json.Serialization;

namespace InvitesBlog.TemplateCompiler;

/// <summary>
/// The manifest.json contract between a compiled template and the platform (§5.2).
/// Declares which variables, roles, gender variants, editable areas, and content blocks
/// the template understands.
/// </summary>
public sealed class TemplateManifest
{
    [JsonPropertyName("slug")] public string Slug { get; set; } = default!;
    [JsonPropertyName("version")] public string Version { get; set; } = default!;
    [JsonPropertyName("variables")] public List<string> Variables { get; set; } = new();
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = new();
    [JsonPropertyName("genderVariants")] public List<string> GenderVariants { get; set; } = new();
    [JsonPropertyName("editableAreas")] public List<string> EditableAreas { get; set; } = new();
    [JsonPropertyName("contentBlocks")] public List<string> ContentBlocks { get; set; } = new();
}

/// <summary>The compiled, ready-to-serve template package (§5.2).</summary>
public sealed class CompiledTemplatePackage
{
    public required TemplateManifest Manifest { get; init; }
    public required string ManifestJson { get; init; }
    public required string IndexHtml { get; init; }
    public required string StylesCss { get; init; }
    public required string TemplateJs { get; init; }

    /// <summary>HTML + CSS + JS byte size — the §5.4 critical-path budget (≤ 300KB).</summary>
    public int CriticalPathBytes { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
    public bool OverBudget => CriticalPathBytes > SceneCompiler.CriticalPathBudgetBytes;
}
