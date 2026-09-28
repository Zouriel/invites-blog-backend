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
    /// <summary>The longest a page can scroll, in canvas units — about a hundred phone screens.</summary>
    public const double MaxPageHeight = 84_400;
    public const int MaxKeyframes = 24;
    /// <summary>The most a keyframe can bring an element to the front.</summary>
    public const int MaxLift = 99;
    /// <summary>Limits on a drawn shape, so one element can't bloat the page.</summary>
    public const int MaxPathContours = 200;
    public const int MaxPathPoints = 2000;
    /// <summary>The furthest photo into a gallery an element can show.</summary>
    public const int MaxGalleryIndex = 50;
    public const int MaxTextLength = 4000;
    public const int MaxSvgBytes = 200 * 1024;
    public const int MaxImageBytes = 400 * 1024;
    public const int MaxRoles = 12;
    public const int MaxThemeEntries = 16;
    public const int MaxCustomFields = 40;
    /// <summary>Continuously visible motion is cheap per element and expensive in bulk — the guide's 56-decorations lesson.</summary>
    public const int AnimatedElementWarning = 60;
    /// <summary>Cycles a loop may play over its track.</summary>
    public const int MaxLoopRepeat = 50;
    /// <summary>Blur radius in canvas units; beyond this a phone spends more than it shows.</summary>
    public const double MaxBlur = 40;
    public const double MaxSkew = 80;
    /// <summary>Pieces a split text is cut into; past this the rest stays one piece.</summary>
    public const int MaxSplitPieces = 400;
    /// <summary>Elements with a blur before Check warns about older phones.</summary>
    public const int BlurWarning = 10;

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

    /// <summary>
    /// A keyframe relative to the element's resting state. The motion extras (3D, skew, blur, clip,
    /// draw, tracking) are absolute, since an element rests at none of them.
    /// </summary>
    public sealed record PresetFrame(
        [property: JsonPropertyName("t")] double T,
        [property: JsonPropertyName("dx")] double Dx = 0,
        [property: JsonPropertyName("dy")] double Dy = 0,
        [property: JsonPropertyName("dRotate")] double DRotate = 0,
        [property: JsonPropertyName("scale")] double ScaleFactor = 1,
        [property: JsonPropertyName("opacity")] double OpacityFactor = 1,
        [property: JsonPropertyName("easing")] string? Easing = null,
        [property: JsonPropertyName("rotateX")] double? RotateX = null,
        [property: JsonPropertyName("rotateY")] double? RotateY = null,
        [property: JsonPropertyName("skewX")] double? SkewX = null,
        [property: JsonPropertyName("blur")] double? Blur = null,
        [property: JsonPropertyName("clip")] double[]? Clip = null,
        [property: JsonPropertyName("draw")] double? Draw = null,
        [property: JsonPropertyName("tracking")] double? Tracking = null);

    /// <param name="Group">Where the picker files it: basic, bounce, zoom, turn, reveal, text.</param>
    /// <param name="Origin">The pivot it needs (swing hangs from the top); null leaves the element's own.</param>
    /// <param name="ClipShape">inset | circle, for presets that cut.</param>
    /// <param name="Only">shape | text when it only means something on one kind of element.</param>
    public sealed record Preset(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("frames")] PresetFrame[] Frames,
        [property: JsonPropertyName("group")] string Group = "basic",
        [property: JsonPropertyName("origin")] double[]? Origin = null,
        [property: JsonPropertyName("clipShape")] string? ClipShape = null,
        [property: JsonPropertyName("only")] string? Only = null,
        [property: JsonPropertyName("split")] DesignSplit? Split = null);

    private const string Out = "ease-out";
    private const string In = "ease-in";
    private const string Spring = "cubic-bezier(0.2, 0.8, 0.2, 1)";
    private const string Back = "cubic-bezier(0.34, 1.56, 0.64, 1)";

    private static PresetFrame F(double t, double dx = 0, double dy = 0, double r = 0, double s = 1, double o = 1, string? e = null) =>
        new(t, dx, dy, r, s, o, e);

    /// <summary>Enter presets play at the start of the track (mostly t 0–0.15); applying one replaces keyframes tagged <c>enter</c>.</summary>
    public static readonly IReadOnlyList<Preset> EnterPresets =
    [
        new("fade", "Fade in", [F(0, o: 0, e: Out), F(0.15)]),
        new("rise", "Rise", [F(0, dy: 40, o: 0, e: Spring), F(0.15)]),
        new("fade-up", "Fade up", [F(0, dy: 40, o: 0, e: Out), F(0.15)]),
        new("fade-down", "Fade down", [F(0, dy: -40, o: 0, e: Out), F(0.15)]),
        new("slide-left", "Slide from right", [F(0, dx: 120, o: 0, e: Out), F(0.15)]),
        new("slide-right", "Slide from left", [F(0, dx: -120, o: 0, e: Out), F(0.15)]),
        new("back-in-up", "Back in, up", [F(0, dy: 120, s: 0.7, o: 0.7, e: Out), F(0.11, dy: -8, e: "ease-in-out"), F(0.15)], "bounce"),
        new("back-in-down", "Back in, down", [F(0, dy: -120, s: 0.7, o: 0.7, e: Out), F(0.11, dy: 8, e: "ease-in-out"), F(0.15)], "bounce"),
        new("back-in-left", "Back in from left", [F(0, dx: -120, s: 0.7, o: 0.7, e: Out), F(0.11, dx: 8, e: "ease-in-out"), F(0.15)], "bounce"),
        new("back-in-right", "Back in from right", [F(0, dx: 120, s: 0.7, o: 0.7, e: Out), F(0.11, dx: -8, e: "ease-in-out"), F(0.15)], "bounce"),
        new("bounce-in", "Bounce in", [F(0, s: 0.3, o: 0, e: Out), F(0.06, s: 1.1, e: "ease-in-out"), F(0.09, s: 0.9, e: "ease-in-out"), F(0.12, s: 1.03, e: "ease-in-out"), F(0.15)], "bounce"),
        new("bounce-in-up", "Bounce in, up", [F(0, dy: 300, o: 0, e: Out), F(0.09, dy: -20, e: "ease-in-out"), F(0.12, dy: 10, e: "ease-in-out"), F(0.15)], "bounce"),
        new("bounce-in-down", "Bounce in, down", [F(0, dy: -300, o: 0, e: Out), F(0.09, dy: 20, e: "ease-in-out"), F(0.12, dy: -10, e: "ease-in-out"), F(0.15)], "bounce"),
        new("bounce-in-left", "Bounce in from left", [F(0, dx: -300, o: 0, e: Out), F(0.09, dx: 20, e: "ease-in-out"), F(0.12, dx: -10, e: "ease-in-out"), F(0.15)], "bounce"),
        new("bounce-in-right", "Bounce in from right", [F(0, dx: 300, o: 0, e: Out), F(0.09, dx: -20, e: "ease-in-out"), F(0.12, dx: 10, e: "ease-in-out"), F(0.15)], "bounce"),
        new("drop", "Drop", [F(0, dy: -400, o: 0, e: In), F(0.09, dy: 8, e: Out), F(0.12, dy: -4, e: In), F(0.15)], "bounce"),
        new("jack-in-the-box", "Jack in the box", [F(0, r: 30, s: 0.1, o: 0, e: Out), F(0.075, r: -10), F(0.11, r: 3), F(0.15)], "bounce", Origin: [0.5, 1]),
        new("pop", "Pop", [F(0, s: 0.4, o: 0, e: Out), F(0.1, s: 1.08), F(0.15)], "zoom"),
        new("zoom-in", "Zoom in", [F(0, s: 0.6, o: 0, e: Out), F(0.15)], "zoom"),
        new("zoom-in-up", "Zoom in, up", [F(0, dy: 600, s: 0.1, o: 0, e: Out), F(0.15)], "zoom"),
        new("zoom-in-down", "Zoom in, down", [F(0, dy: -600, s: 0.1, o: 0, e: Out), F(0.15)], "zoom"),
        new("zoom-in-left", "Zoom in from left", [F(0, dx: -600, s: 0.1, o: 0, e: Out), F(0.15)], "zoom"),
        new("zoom-in-right", "Zoom in from right", [F(0, dx: 600, s: 0.1, o: 0, e: Out), F(0.15)], "zoom"),
        new("grow-from-corner", "Grow from corner", [F(0, s: 0, o: 0, e: Back), F(0.15)], "zoom", Origin: [0, 1]),
        new("spin-in", "Spin in", [F(0, r: -90, s: 0.5, o: 0, e: Out), F(0.15)], "turn"),
        new("spin-zoom", "Spin and zoom", [F(0, r: -180, s: 0, o: 0, e: Out), F(0.15)], "turn"),
        new("roll-in", "Roll in", [F(0, dx: -300, r: -120, o: 0, e: Out), F(0.15)], "turn"),
        new("swing-in", "Swing in", [F(0, r: -15, o: 0, e: "ease-in-out"), F(0.06, r: 10), F(0.1, r: -5), F(0.15)], "turn", Origin: [0.5, 0]),
        new("flip-in-x", "Flip in (top over)", [new(0, OpacityFactor: 0, Easing: Out, RotateX: 90), new(0.15, RotateX: 0)], "turn"),
        new("flip-in-y", "Flip in (side over)", [new(0, OpacityFactor: 0, Easing: Out, RotateY: 90), new(0.15, RotateY: 0)], "turn"),
        new("speed-in", "Speed in", [new(0, Dx: 200, OpacityFactor: 0, Easing: Out, SkewX: -30), new(0.1, SkewX: 10), new(0.15, SkewX: 0)], "turn"),
        new("wipe-in-right", "Wipe in, left to right", [new(0, Easing: Out, Clip: [0, 100, 0, 0]), new(0.15, Clip: [0, 0, 0, 0])], "reveal", ClipShape: "inset"),
        new("wipe-in-left", "Wipe in, right to left", [new(0, Easing: Out, Clip: [0, 0, 0, 100]), new(0.15, Clip: [0, 0, 0, 0])], "reveal", ClipShape: "inset"),
        new("wipe-in-up", "Wipe in, bottom up", [new(0, Easing: Out, Clip: [100, 0, 0, 0]), new(0.15, Clip: [0, 0, 0, 0])], "reveal", ClipShape: "inset"),
        new("wipe-in-down", "Wipe in, top down", [new(0, Easing: Out, Clip: [0, 0, 100, 0]), new(0.15, Clip: [0, 0, 0, 0])], "reveal", ClipShape: "inset"),
        new("iris-in", "Iris open", [new(0, Easing: Out, Clip: [0]), new(0.15, Clip: [71])], "reveal", ClipShape: "circle"),
        new("blur-in", "Blur in", [new(0, OpacityFactor: 0, Easing: Out, Blur: 12), new(0.15, Blur: 0)], "reveal"),
        new("draw-on", "Draw on", [new(0, Easing: "ease-in-out", Draw: 0), new(0.3, Draw: 1)], "reveal", Only: "shape"),
        new("tracking-in", "Letters close in", [new(0, OpacityFactor: 0, Easing: Out, Tracking: 0.5), new(0.15, Tracking: 0)], "text", Only: "text"),
        // Split presets: the frames play per piece, over each piece's own share of the track.
        new("letter-rise", "Letter by letter", [F(0, dy: 20, o: 0, e: Spring), F(0.3)], "text", Only: "text", Split: new DesignSplit { By = "letter", Stagger = 0.6 }),
        new("word-fade", "Word by word", [F(0, dy: 8, o: 0, e: Out), F(0.3)], "text", Only: "text", Split: new DesignSplit { By = "word", Stagger = 0.5 }),
        new("typewriter", "Typewriter", [F(0, o: 0), F(0.02)], "text", Only: "text", Split: new DesignSplit { By = "letter", Stagger = 0.85 }),
        new("scatter-in", "Letters gather", [F(0, dx: -30, dy: 40, r: -40, o: 0, e: Out), F(0.3)], "text", Only: "text", Split: new DesignSplit { By = "letter", Stagger = 0.5 }),
    ];

    /// <summary>Exit presets play at the end of the track (mostly t 0.85–1); applying one replaces keyframes tagged <c>exit</c>.</summary>
    public static readonly IReadOnlyList<Preset> ExitPresets =
    [
        new("fade", "Fade out", [F(0.85, e: In), F(1, o: 0)]),
        new("sink", "Sink", [F(0.85, e: In), F(1, dy: 40, o: 0)]),
        new("fade-up", "Fade up and out", [F(0.85, e: In), F(1, dy: -40, o: 0)]),
        new("fade-down", "Fade down and out", [F(0.85, e: In), F(1, dy: 40, o: 0)]),
        new("slide-left", "Slide out left", [F(0.85, e: In), F(1, dx: -120, o: 0)]),
        new("slide-right", "Slide out right", [F(0.85, e: In), F(1, dx: 120, o: 0)]),
        new("back-out-up", "Back out, up", [F(0.85, e: "ease-in-out"), F(0.89, dy: 8, e: In), F(1, dy: -120, s: 0.7, o: 0.7)], "bounce"),
        new("back-out-down", "Back out, down", [F(0.85, e: "ease-in-out"), F(0.89, dy: -8, e: In), F(1, dy: 120, s: 0.7, o: 0.7)], "bounce"),
        new("bounce-out", "Bounce out", [F(0.85, e: "ease-in-out"), F(0.88, s: 0.9, e: "ease-in-out"), F(0.92, s: 1.1, e: In), F(1, s: 0.3, o: 0)], "bounce"),
        new("drop-out", "Fall away", [F(0.85, e: In), F(0.88, dy: -10, e: In), F(1, dy: 500, r: 12, o: 0)], "bounce"),
        new("hinge", "Hinge", [F(0.85, e: "ease-in-out"), F(0.89, r: 80, e: "ease-in-out"), F(0.92, r: 60, e: "ease-in-out"), F(0.95, r: 80, e: In), F(1, dy: 700, r: 80, o: 0)], "bounce", Origin: [0, 0]),
        new("zoom-out", "Zoom out", [F(0.85, e: In), F(1, s: 0.6, o: 0)], "zoom"),
        new("zoom-out-up", "Zoom out, up", [F(0.85, e: In), F(1, dy: -600, s: 0.1, o: 0)], "zoom"),
        new("zoom-out-down", "Zoom out, down", [F(0.85, e: In), F(1, dy: 600, s: 0.1, o: 0)], "zoom"),
        new("spin-zoom-out", "Spin and shrink", [F(0.85, e: In), F(1, r: 180, s: 0, o: 0)], "turn"),
        new("roll-out", "Roll out", [F(0.85, e: In), F(1, dx: 300, r: 120, o: 0)], "turn"),
        new("swing-out", "Swing out", [F(0.85, e: "ease-in-out"), F(0.9, r: 10), F(0.94, r: -15), F(1, r: 30, o: 0)], "turn", Origin: [0.5, 0]),
        new("flip-out-x", "Flip out (top over)", [new(0.85, Easing: In, RotateX: 0), new(1, OpacityFactor: 0, RotateX: 90)], "turn"),
        new("flip-out-y", "Flip out (side over)", [new(0.85, Easing: In, RotateY: 0), new(1, OpacityFactor: 0, RotateY: 90)], "turn"),
        new("wipe-out-right", "Wipe away, left to right", [new(0.85, Easing: In, Clip: [0, 0, 0, 0]), new(1, Clip: [0, 0, 0, 100])], "reveal", ClipShape: "inset"),
        new("wipe-out-up", "Wipe away upwards", [new(0.85, Easing: In, Clip: [0, 0, 0, 0]), new(1, Clip: [0, 0, 100, 0])], "reveal", ClipShape: "inset"),
        new("iris-out", "Iris close", [new(0.85, Easing: In, Clip: [71]), new(1, Clip: [0])], "reveal", ClipShape: "circle"),
        new("blur-out", "Blur out", [new(0.85, Easing: In, Blur: 0), new(1, OpacityFactor: 0, Blur: 12)], "reveal"),
        new("tracking-out", "Letters drift apart", [new(0.85, Easing: In, Tracking: 0), new(1, OpacityFactor: 0, Tracking: 0.5)], "text", Only: "text"),
    ];

    /// <summary>One cycle of a loop, relative: offsets in canvas units and degrees, scale and opacity as multipliers.</summary>
    public sealed record LoopPresetFrame(
        [property: JsonPropertyName("t")] double T,
        [property: JsonPropertyName("dx")] double Dx = 0,
        [property: JsonPropertyName("dy")] double Dy = 0,
        [property: JsonPropertyName("rotate")] double Rotate = 0,
        [property: JsonPropertyName("scale")] double Scale = 1,
        [property: JsonPropertyName("opacity")] double Opacity = 1,
        [property: JsonPropertyName("easing")] string? Easing = null);

    /// <param name="Repeat">Cycles over the track by default.</param>
    public sealed record LoopPreset(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("label")] string Label,
        [property: JsonPropertyName("frames")] LoopPresetFrame[] Frames,
        [property: JsonPropertyName("repeat")] int Repeat,
        [property: JsonPropertyName("alternate")] bool Alternate = false,
        [property: JsonPropertyName("origin")] double[]? Origin = null,
        [property: JsonPropertyName("use")] string? Use = null);

    private static LoopPresetFrame L(double t, double dx = 0, double dy = 0, double r = 0, double s = 1, double o = 1, string? e = null) =>
        new(t, dx, dy, r, s, o, e);

    private const string Soft = "ease-in-out";

    /// <summary>Emphasis: motion that repeats while the element is on screen, on top of its keyframes.</summary>
    public static readonly IReadOnlyList<LoopPreset> LoopPresets =
    [
        new("float", "Float", [L(0, e: Soft), L(0.5, dy: -10, e: Soft), L(1)], 4, Use: "Balloons, clouds, hearts"),
        new("sway", "Sway", [L(0, r: -4, e: Soft), L(1, r: 4)], 6, Alternate: true, Origin: [0.5, 0], Use: "Lanterns, leaves, tags"),
        new("pulse", "Pulse", [L(0, e: Soft), L(0.5, s: 1.06, e: Soft), L(1)], 6, Use: "The RSVP button, hearts"),
        new("heartbeat", "Heartbeat", [L(0, e: Soft), L(0.14, s: 1.3, e: Soft), L(0.28, e: Soft), L(0.42, s: 1.3, e: Soft), L(0.7)], 4, Use: "Hearts"),
        new("twinkle", "Twinkle", [L(0, s: 0.8, o: 0.3, e: Soft), L(0.5, s: 1.2, e: Soft), L(1, s: 0.8, o: 0.3)], 6, Use: "Stars, sparkles"),
        new("spin", "Spin", [L(0), L(1, r: 360)], 2, Use: "Seals, suns, ornaments"),
        new("orbit", "Orbit", [.. Enumerable.Range(0, 9).Select(i => L(i / 8.0,
            dx: Math.Round(12 * Math.Cos(i * Math.PI / 4), 2), dy: Math.Round(12 * Math.Sin(i * Math.PI / 4), 2)))], 3, Use: "Small things circling a monogram"),
        new("drift", "Drift", [L(0, e: Soft), L(1, dx: 30, dy: -20)], 2, Alternate: true, Use: "Clouds, dust"),
        new("flicker", "Flicker", [L(0), L(0.1, o: 0.6), L(0.2), L(0.35, o: 0.8), L(0.5), L(0.7, o: 0.7), L(0.8), L(1)], 6, Use: "Candles, fairy lights"),
        new("swing", "Swing", [L(0, e: Soft), L(0.2, r: 15, e: Soft), L(0.4, r: -10, e: Soft), L(0.6, r: 5, e: Soft), L(0.8, r: -5, e: Soft), L(1)], 3, Origin: [0.5, 0], Use: "Hanging ornaments"),
        new("wiggle", "Wiggle", [L(0, r: -3, e: Soft), L(1, r: 3)], 10, Alternate: true, Use: "Stickers"),
        new("wobble", "Wobble", [L(0), L(0.15, dx: -25, r: -5), L(0.3, dx: 20, r: 3), L(0.45, dx: -15, r: -3), L(0.6, dx: 10, r: 2), L(0.75, dx: -5, r: -1), L(1)], 2, Use: "Playful"),
        new("jello", "Jello", [L(0), L(0.3, s: 1.05), L(0.4, s: 0.95), L(0.5, r: 2, s: 1.03), L(0.65, r: -2, s: 0.98), L(0.75, s: 1.01), L(1)], 2, Use: "Playful"),
        new("tada", "Tada", [L(0), L(0.1, r: -3, s: 0.9), L(0.3, r: 3, s: 1.1), L(0.4, r: -3, s: 1.1), L(0.5, r: 3, s: 1.1), L(0.6, r: -3, s: 1.1), L(0.7, r: 3, s: 1.1), L(0.8, r: -3, s: 1.1), L(0.9, r: 3, s: 1.1), L(1)], 2, Use: "Look here"),
        new("bob", "Bob", [L(0, e: Out), L(0.5, dy: 8, e: In), L(1)], 8, Use: "“Scroll down” hints"),
    ];

    // ----- Element types -----

    public static readonly string[] ElementTypes = ["text", "shape", "svg", "image", "slot", "rsvp", "link", "dress", "group"];

    /// <summary>The paths a link element may point at, besides <c>event.*</c> url fields.</summary>
    public static readonly string[] LinkPaths = ["camera.link", "photos.link", "event.venue.mapLink"];
}
