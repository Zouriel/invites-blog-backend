using System.Text.Json.Serialization;

namespace InvitesBlog.TemplateCompiler.Design;

/// <summary>
/// Everything the designer offers, in one place, so the editor and the compiler agree by
/// construction: the editor reads this catalog from the API rather than keeping its own copy.
/// </summary>
public static class DesignCatalog
{
    // ----- Limits -----

    public const int MaxElements = 300;
    public const int MaxDepth = 3;
    public const int MaxSections = 30;
    public const double MinSectionHeight = 200;
    public const double MaxSectionHeight = 6000;
    public const int MaxKeyframes = 24;
    public const int MaxTextLength = 4000;
    public const int MaxSvgBytes = 200 * 1024;
    public const int MaxImageBytes = 400 * 1024;
    public const int MaxRoles = 12;
    public const int MaxThemeEntries = 16;
    public const int MaxCustomFields = 40;
    /// <summary>Continuously visible motion is cheap per element and expensive in bulk — the guide's 56-decorations lesson.</summary>
    public const int AnimatedElementWarning = 60;

    public static readonly string[] RequiredThemeKeys = ["accent", "bg", "text"];

    public static readonly string[] FieldTypes = ["text", "textarea", "date", "time", "url", "color", "select"];

    public static readonly string[] Easings = ["linear", "ease", "ease-in", "ease-out", "ease-in-out"];

    // ----- Fonts -----

    public sealed record Font(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("category")] string Category,
        [property: JsonPropertyName("fallback")] string Fallback,
        [property: JsonPropertyName("weights")] int[] Weights)
    {
        /// <summary>The CSS <c>font-family</c> value.</summary>
        [JsonPropertyName("stack")] public string Stack => $"\"{Name}\", {Fallback}";
    }

    /// <summary>
    /// Self-hosted (SIL Open Font License) Latin subsets, published under <c>fonts/</c> in storage.
    /// Self-hosted because the render CSP is <c>font-src 'self' data:</c> — a font CDN would be blocked.
    /// </summary>
    public static readonly IReadOnlyList<Font> Fonts =
    [
        new("playfair-display", "Playfair Display", "serif", "Georgia, serif", [400, 700]),
        new("cormorant-garamond", "Cormorant Garamond", "serif", "Georgia, serif", [400, 600]),
        new("lora", "Lora", "serif", "Georgia, serif", [400, 700]),
        new("libre-baskerville", "Libre Baskerville", "serif", "Georgia, serif", [400, 700]),
        new("cinzel", "Cinzel", "display", "Georgia, serif", [400, 700]),
        new("italiana", "Italiana", "display", "Georgia, serif", [400]),
        new("inter", "Inter", "sans", "system-ui, sans-serif", [400, 600]),
        new("montserrat", "Montserrat", "sans", "system-ui, sans-serif", [400, 600]),
        new("josefin-sans", "Josefin Sans", "sans", "system-ui, sans-serif", [400, 600]),
        new("poppins", "Poppins", "sans", "system-ui, sans-serif", [400, 600]),
        new("great-vibes", "Great Vibes", "script", "cursive", [400]),
        new("parisienne", "Parisienne", "script", "cursive", [400]),
        new("dancing-script", "Dancing Script", "script", "cursive", [400, 700]),
        new("allura", "Allura", "script", "cursive", [400]),
    ];

    public static Font? FindFont(string? id) =>
        id is null ? null : Fonts.FirstOrDefault(f => f.Id == id);

    /// <summary>The storage key a font file is published under.</summary>
    public static string FontKey(string id, int weight) => $"fonts/{id}-{weight}.woff2";

    // ----- Variables -----

    /// <param name="Kind">text (a data-var), link (a data-href) or image (a data-src).</param>
    public sealed record Variable(
        [property: JsonPropertyName("path")] string Path,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("group")] string Group,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("sample")] string Sample);

    /// <summary>The template guide's "paths you can use", with what the editor shows for each.</summary>
    public static readonly IReadOnlyList<Variable> Variables =
    [
        new("guest.name", "Guest name", "Guest", "text", "text", "Aisha & Family"),
        new("guest.role", "Guest role", "Guest", "text", "text", "Family"),

        new("event.title", "Title", "Event", "text", "text", "Hana & Imran"),
        new("event.subtitle", "Subtitle", "Event", "text", "text", "are getting married"),
        new("event.description", "Description", "Event", "text", "textarea",
            "We would love for you to celebrate with us as we begin our life together."),
        new("event.date", "Date", "Event", "text", "date", "Saturday, 28 August 2027"),
        new("event.time", "Time", "Event", "text", "time", "6:30 PM"),
        new("event.schedule", "Schedule", "Event", "text", "textarea", "6:30 Welcome · 7:30 Dinner · 9:00 Dancing"),
        new("event.dressCode", "Dress code", "Event", "text", "text", "Black tie"),
        new("event.hashtag", "Hashtag", "Event", "text", "text", "#HanaAndImran"),

        new("event.venue.name", "Venue", "Venue", "text", "text", "The Palm Pavilion"),
        new("event.venue.address", "Address", "Venue", "text", "textarea", "12 Lagoon Road, Malé"),
        new("event.venue.mapLink", "Map link", "Venue", "link", "url", "https://maps.example.com"),

        new("inviter.name", "Inviter name", "Inviter", "text", "text", "The Rasheed family"),
        new("inviter.phone", "Inviter phone", "Inviter", "text", "text", "+960 777 1234"),
        new("inviter.email", "Inviter email", "Inviter", "text", "text", "hello@example.com"),

        new("rsvp.link", "RSVP", "Platform", "link", "url", "#rsvp"),
        new("camera.link", "Camera", "Platform", "link", "url", "#camera"),
        new("photos.link", "Photos", "Platform", "link", "url", "#photos"),

        new("event.coverImage", "Cover photo", "Images", "image", "image", ""),
        new("event.couplePhoto", "Couple photo", "Images", "image", "image", ""),
        new("event.gallery", "Photo gallery", "Images", "image", "image", ""),
    ];

    public static Variable? FindVariable(string path) =>
        Variables.FirstOrDefault(v => string.Equals(v.Path, path, StringComparison.Ordinal));

    // ----- Motion presets -----

    /// <summary>A keyframe relative to the element's resting state.</summary>
    public sealed record PresetFrame(
        [property: JsonPropertyName("t")] double T,
        [property: JsonPropertyName("dx")] double Dx = 0,
        [property: JsonPropertyName("dy")] double Dy = 0,
        [property: JsonPropertyName("dRotate")] double DRotate = 0,
        [property: JsonPropertyName("scale")] double ScaleFactor = 1,
        [property: JsonPropertyName("opacity")] double OpacityFactor = 1,
        [property: JsonPropertyName("easing")] string? Easing = null);

    public sealed record Preset(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("frames")] PresetFrame[] Frames);

    /// <summary>Enter presets occupy t ∈ [0, 0.15]; applying one replaces keyframes tagged <c>enter</c>.</summary>
    public static readonly IReadOnlyList<Preset> EnterPresets =
    [
        new("fade", "Fade in", [new(0, OpacityFactor: 0, Easing: "ease-out"), new(0.15)]),
        new("fade-up", "Fade up", [new(0, Dy: 40, OpacityFactor: 0, Easing: "ease-out"), new(0.15)]),
        new("fade-down", "Fade down", [new(0, Dy: -40, OpacityFactor: 0, Easing: "ease-out"), new(0.15)]),
        new("slide-left", "Slide from right", [new(0, Dx: 120, OpacityFactor: 0, Easing: "ease-out"), new(0.15)]),
        new("slide-right", "Slide from left", [new(0, Dx: -120, OpacityFactor: 0, Easing: "ease-out"), new(0.15)]),
        new("zoom-in", "Zoom in", [new(0, ScaleFactor: 0.6, OpacityFactor: 0, Easing: "ease-out"), new(0.15)]),
        new("pop", "Pop", [new(0, ScaleFactor: 0.4, OpacityFactor: 0, Easing: "ease-out"), new(0.1, ScaleFactor: 1.08), new(0.15)]),
        new("spin-in", "Spin in", [new(0, DRotate: -90, ScaleFactor: 0.5, OpacityFactor: 0, Easing: "ease-out"), new(0.15)]),
    ];

    /// <summary>Exit presets occupy t ∈ [0.85, 1]; applying one replaces keyframes tagged <c>exit</c>.</summary>
    public static readonly IReadOnlyList<Preset> ExitPresets =
    [
        new("fade", "Fade out", [new(0.85, Easing: "ease-in"), new(1, OpacityFactor: 0)]),
        new("fade-up", "Fade up and out", [new(0.85, Easing: "ease-in"), new(1, Dy: -40, OpacityFactor: 0)]),
        new("fade-down", "Fade down and out", [new(0.85, Easing: "ease-in"), new(1, Dy: 40, OpacityFactor: 0)]),
        new("slide-left", "Slide out left", [new(0.85, Easing: "ease-in"), new(1, Dx: -120, OpacityFactor: 0)]),
        new("slide-right", "Slide out right", [new(0.85, Easing: "ease-in"), new(1, Dx: 120, OpacityFactor: 0)]),
        new("zoom-out", "Zoom out", [new(0.85, Easing: "ease-in"), new(1, ScaleFactor: 0.6, OpacityFactor: 0)]),
    ];

    // ----- Element types -----

    public static readonly string[] ElementTypes = ["text", "shape", "svg", "image", "slot", "rsvp", "link", "dress", "group"];

    /// <summary>The paths a link element may point at, besides <c>event.*</c> url fields.</summary>
    public static readonly string[] LinkPaths = ["camera.link", "photos.link", "event.venue.mapLink"];
}
