using System.Text.Json.Serialization;

namespace InvitesBlog.TemplateCompiler;

/// <summary>
/// The declarative scene description authored by the designer (§6.1). The compiler turns it into
/// the trusted HTML/CSS/JS package of §5. No raw HTML/JS ever appears here — only structured data.
/// This is the single rendering source for both platform and community templates.
/// </summary>
public sealed class Scene
{
    [JsonPropertyName("slug")] public string Slug { get; set; } = "untitled";
    [JsonPropertyName("version")] public string Version { get; set; } = "1.0.0";
    [JsonPropertyName("name")] public string Name { get; set; } = "Untitled Template";
    [JsonPropertyName("category")] public string Category { get; set; } = "Custom Event";
    [JsonPropertyName("theme")] public SceneTheme Theme { get; set; } = new();
    [JsonPropertyName("envelope")] public SceneEnvelope Envelope { get; set; } = new();
    [JsonPropertyName("sections")] public List<SceneSection> Sections { get; set; } = new();
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = new();
    [JsonPropertyName("genderVariants")] public List<string> GenderVariants { get; set; } = new();
    [JsonPropertyName("customVariables")] public List<string> CustomVariables { get; set; } = new();
}

public sealed class SceneTheme
{
    [JsonPropertyName("primary")] public string Primary { get; set; } = "#b8860b";
    [JsonPropertyName("accent")] public string Accent { get; set; } = "#d4af37";
    [JsonPropertyName("background")] public string Background { get; set; } = "#faf7f0";
    [JsonPropertyName("surface")] public string Surface { get; set; } = "#ffffff";
    [JsonPropertyName("text")] public string Text { get; set; } = "#2b2b2b";
    [JsonPropertyName("headingFont")] public string HeadingFont { get; set; } = "'Cormorant Garamond', Georgia, serif";
    [JsonPropertyName("bodyFont")] public string BodyFont { get; set; } = "'Inter', system-ui, sans-serif";
    /// <summary>subtle | balanced | dramatic (§6.2 intensity slider).</summary>
    [JsonPropertyName("intensity")] public string Intensity { get; set; } = "balanced";
}

public sealed class SceneEnvelope
{
    /// <summary>flapLift | waxSeal | slideOut | foldOut (§6.2).</summary>
    [JsonPropertyName("style")] public string Style { get; set; } = "flapLift";
    [JsonPropertyName("label")] public string Label { get; set; } = "You're invited";
}

public sealed class SceneSection
{
    [JsonPropertyName("id")] public string Id { get; set; } = default!;
    /// <summary>hero | story | schedule | dressCode | gallery | venue | roleBlock | text | rsvp.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    /// <summary>centered | split | fullBleed.</summary>
    [JsonPropertyName("layout")] public string Layout { get; set; } = "centered";
    /// <summary>fade-up | curtain | parallax | colorWash | letterByLetter | kenBurns.</summary>
    [JsonPropertyName("reveal")] public string Reveal { get; set; } = "fade-up";
    [JsonPropertyName("content")] public Dictionary<string, string> Content { get; set; } = new();
    [JsonPropertyName("visibility")] public SceneVisibility Visibility { get; set; } = new();
}

public sealed class SceneVisibility
{
    /// <summary>always | block (gated by a §12 content block resolved server-side).</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "always";
    [JsonPropertyName("block")] public string? Block { get; set; }
}
