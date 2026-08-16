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

    /// <summary>
    /// The theming surface the template exposes as CSS custom properties (<c>--ib-accent</c>,
    /// <c>--ib-bg</c>, <c>--ib-text</c>, …) plus any selectable fonts. The wizard's theming step renders
    /// one control per <see cref="TemplateTheme.Keys"/> entry, pre-filled with the authored default.
    /// </summary>
    [JsonPropertyName("theme")] public TemplateTheme Theme { get; set; } = new();

    /// <summary>
    /// The formalized role list: one entry per slug in <see cref="Roles"/>, carrying which theme keys,
    /// fields and image slots are scoped to that role (everything not listed here is shared across roles).
    /// </summary>
    [JsonPropertyName("roleDefinitions")] public List<TemplateRoleDefinition> RoleDefinitions { get; set; } = new();
}

/// <summary>One fillable text/link field on a template.</summary>
public sealed class TemplateFieldSlot
{
    /// <summary>The <c>data-var</c>/<c>data-href</c> path the value is injected at, e.g. <c>event.title</c>.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = default!;
    /// <summary>Label shown next to the input (from <c>data-field-label</c>, else derived from the key).</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = default!;
    /// <summary>
    /// Widget hint: <c>text</c> | <c>textarea</c> | <c>date</c> | <c>time</c> | <c>url</c> |
    /// <c>color</c> | <c>select</c> | <c>image</c>. From <c>data-type</c> (or legacy
    /// <c>data-field-type</c>), else inferred from the key.
    /// </summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    /// <summary>Allowed values for <c>type="select"</c>, from <c>data-options</c>. Null for other types.</summary>
    [JsonPropertyName("options")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Options { get; set; }
    /// <summary>Role slug this field belongs to (from <c>data-role-scope</c>); null means shared by all roles.</summary>
    [JsonPropertyName("roleScope")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoleScope { get; set; }
}

/// <summary>One fillable image on a template — a <c>data-src</c> path plus a human label for the builder.</summary>
public sealed class TemplateImageSlot
{
    /// <summary>The <c>data-src</c> path the image URL is injected at, e.g. <c>event.coverImage</c>.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = default!;
    /// <summary>Label shown next to the file picker (from <c>data-slot-label</c>, else derived from the key).</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = default!;
    /// <summary>
    /// True when the slot accepts a GALLERY of images (<c>data-multiple="true"</c>) rather than exactly
    /// one — the builder then manages an ordered list for this key.
    /// </summary>
    [JsonPropertyName("multiple")] public bool Multiple { get; set; }
    /// <summary>Minimum images for a multi-image slot (<c>data-min-images</c>); null = unbounded.</summary>
    [JsonPropertyName("minImages")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MinImages { get; set; }
    /// <summary>Maximum images for a multi-image slot (<c>data-max-images</c>); null = unbounded.</summary>
    [JsonPropertyName("maxImages")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? MaxImages { get; set; }
    /// <summary>Role slug this slot belongs to (from <c>data-role-scope</c>); null means shared by all roles.</summary>
    [JsonPropertyName("roleScope")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RoleScope { get; set; }
}

/// <summary>
/// The template's declared theming surface. Every template exposes at minimum an accent, background and
/// text colour as CSS custom properties; the packager extracts the declared properties and their authored
/// defaults so the wizard can offer them as real controls without hardcoding any template's palette.
/// </summary>
public sealed class TemplateTheme
{
    /// <summary>Authored default of <c>--ib-accent</c>, when declared.</summary>
    [JsonPropertyName("accentColor")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AccentColor { get; set; }
    /// <summary>Authored default of <c>--ib-bg</c>, when declared.</summary>
    [JsonPropertyName("backgroundColor")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? BackgroundColor { get; set; }
    /// <summary>Authored default of <c>--ib-text</c>, when declared.</summary>
    [JsonPropertyName("textColor")] [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TextColor { get; set; }
    /// <summary>Font families the inviter may choose between (from <c>&lt;meta name="ib-fonts"&gt;</c>).</summary>
    [JsonPropertyName("fonts")] public List<string> Fonts { get; set; } = new();
    /// <summary>Every declared <c>--ib-*</c> custom property, in author order — the wizard's control list.</summary>
    [JsonPropertyName("keys")] public List<TemplateThemeKey> Keys { get; set; } = new();
}

/// <summary>One themable CSS custom property the template declares.</summary>
public sealed class TemplateThemeKey
{
    /// <summary>Camel-cased manifest key the override is stored under, e.g. <c>accentColor</c>.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = default!;
    /// <summary>The CSS custom property it drives, e.g. <c>--ib-accent</c>.</summary>
    [JsonPropertyName("cssVar")] public string CssVar { get; set; } = default!;
    /// <summary>Human label for the theming step.</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = default!;
    /// <summary>Control kind: <c>color</c> | <c>font</c> | <c>text</c>.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "color";
    /// <summary>The value authored in the template — what the control is pre-filled with.</summary>
    [JsonPropertyName("default")] public string Default { get; set; } = default!;
}

/// <summary>One role the template supports, plus everything scoped to it.</summary>
public sealed class TemplateRoleDefinition
{
    /// <summary>Role slug, e.g. <c>bride</c>.</summary>
    [JsonPropertyName("slug")] public string Slug { get; set; } = default!;
    /// <summary>Human label derived from the slug.</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = default!;
    /// <summary>Theme keys this role may override independently (all of <see cref="TemplateTheme.Keys"/> by default).</summary>
    [JsonPropertyName("themeKeys")] public List<string> ThemeKeys { get; set; } = new();
    /// <summary>Field keys scoped to this role via <c>data-role-scope</c>.</summary>
    [JsonPropertyName("fields")] public List<string> Fields { get; set; } = new();
    /// <summary>Image-slot keys scoped to this role via <c>data-role-scope</c>.</summary>
    [JsonPropertyName("imageSlots")] public List<string> ImageSlots { get; set; } = new();
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
