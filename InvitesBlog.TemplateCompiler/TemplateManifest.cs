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

    /// <summary>
    /// Image slots the inviter fills in the builder — one per <c>data-src</c> path in the template.
    /// The inviter picks an image for each; its URL is injected at the slot's <see cref="TemplateImageSlot.Key"/> path.
    /// </summary>
    [JsonPropertyName("imageSlots")] public List<TemplateImageSlot> ImageSlots { get; set; } = new();

    /// <summary>
    /// Text/link fields the inviter fills in the builder — one per <c>data-var</c>/<c>data-href</c> path.
    /// The builder renders an input per field and shows only the fields this template actually declares,
    /// so authors can add arbitrary fields without any code change (§ dynamic builder).
    /// </summary>
    [JsonPropertyName("fields")] public List<TemplateFieldSlot> Fields { get; set; } = new();
}

/// <summary>One fillable text/link field on a template.</summary>
public sealed class TemplateFieldSlot
{
    /// <summary>The <c>data-var</c>/<c>data-href</c> path the value is injected at, e.g. <c>event.title</c>.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = default!;
    /// <summary>Label shown next to the input (from <c>data-field-label</c>, else derived from the key).</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = default!;
    /// <summary>Widget hint: <c>text</c> | <c>textarea</c> | <c>date</c> | <c>time</c> | <c>url</c>.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
}

/// <summary>One fillable image on a template — a <c>data-src</c> path plus a human label for the builder.</summary>
public sealed class TemplateImageSlot
{
    /// <summary>The <c>data-src</c> path the image URL is injected at, e.g. <c>event.coverImage</c>.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = default!;
    /// <summary>Label shown next to the file picker (from <c>data-slot-label</c>, else derived from the key).</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = default!;
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
