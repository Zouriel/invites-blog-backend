using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvitesBlog.TemplateCompiler.Design;

/// <summary>
/// The scene a customer builds in the visual template designer. It is the source of truth for a
/// designed template; the HTML is always derived from it by <see cref="DesignCompiler"/>, on the
/// server, so nothing a browser sends as markup ever reaches a guest.
///
/// <para><b>Units.</b> Everything is measured in canvas units on a phone-shaped page
/// <see cref="DesignCanvas.Width"/> (390) units wide. The compiled page maps one unit to
/// <c>min(100vw, 480px) / 390</c>, so it scales with the screen's WIDTH and never with its height:
/// the viewport-height remapping the template guide warns about cannot happen.</para>
///
/// <para><b>Scroll.</b> A track's <c>start</c>/<c>end</c> are scroll offsets in the same units —
/// "how far the reader has scrolled", from 0 to <see cref="ScrollRange"/>. They are
/// emitted as length-based <c>animation-range</c>s, so a taller or shorter phone changes where the
/// page ENDS, never where an animation happens.</para>
///
/// <para><b>Length.</b> There are no screens: the page ends where its last element does — the moment
/// its bottom reaches the bottom of the reference phone, counting how long a pinned element holds and
/// where the last motion track ends. Place something further down and the page grows to it.</para>
///
/// <para>Deliberately unrelated to the older section-based <see cref="Scene"/> used by the seeded
/// platform templates; the <see cref="Schema"/> number tells the two apart.</para>
/// </summary>
public sealed class DesignScene
{
    public const int CurrentSchema = 3;

    [JsonPropertyName("schema")] public int Schema { get; set; } = CurrentSchema;
    [JsonPropertyName("canvas")] public DesignCanvas Canvas { get; set; } = new();

    /// <summary>
    /// "saveTheDate" for a save the date, which asks nothing — no reply button needed, and none shown
    /// (the server adds "Add to calendar" instead). Null or anything else is an invitation.
    /// </summary>
    [JsonPropertyName("kind")] public string? Kind { get; set; }

    [JsonIgnore] public bool IsSaveTheDate => Kind == "saveTheDate";

    /// <summary>Theme keys, each becoming <c>--ib-{key}</c>. <c>accent</c>, <c>bg</c> and <c>text</c> are required.</summary>
    [JsonPropertyName("theme")] public List<DesignThemeEntry> Theme { get; set; } = new();

    /// <summary>Font ids from <see cref="DesignCatalog.Fonts"/> offered to the inviter (the <c>ib-fonts</c> meta).</summary>
    [JsonPropertyName("fonts")] public List<string> Fonts { get; set; } = new();

    /// <summary>Role names the template understands (the <c>ib-roles</c> meta).</summary>
    [JsonPropertyName("roles")] public List<string> Roles { get; set; } = new();

    /// <summary>Custom <c>event.*</c> fields the author invented, with how the inviter fills them.</summary>
    [JsonPropertyName("fields")] public List<DesignCustomField> Fields { get; set; } = new();

    [JsonPropertyName("elements")] public List<DesignElement> Elements { get; set; } = new();

    /// <summary>Imported SVGs and pictures, referenced by id so a reused asset is embedded once.</summary>
    [JsonPropertyName("assets")] public Dictionary<string, DesignAsset> Assets { get; set; } = new();

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.Strict,
        MaxDepth = 32,
    };

    /// <summary>Parses a scene, or throws <see cref="DesignSceneException"/> with a readable reason.</summary>
    public static DesignScene Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new DesignSceneException("The design is empty.");
        try
        {
            var scene = JsonSerializer.Deserialize<DesignScene>(json, Json)
                        ?? throw new DesignSceneException("The design is empty.");
            DesignSceneUpgrade.Apply(scene);
            return scene;
        }
        catch (JsonException e)
        {
            throw new DesignSceneException($"The design couldn't be read: {e.Message}");
        }
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>How tall the page is: its last element's end, never less than one reference screen.</summary>
    public double PageHeight() => ScrollRange() + DesignCanvas.ReferenceViewport;

    /// <summary>
    /// How far the reference phone scrolls: until the lowest element's bottom meets the bottom of the
    /// screen. A pinned element counts where it lets go, since it has travelled down with the reader.
    /// And on to where the last motion track ends, so an element's exit plays before the page stops —
    /// the page ends where its last element's bar on the editor's timeline does.
    /// </summary>
    public double ScrollRange()
    {
        double bottom = 0;
        foreach (var el in Elements)
        {
            if (!double.IsFinite(el.Y) || !double.IsFinite(el.H)) continue;
            var end = el.Y + Math.Max(0, el.H);
            if (el.Pinned && el.Track is { } t && double.IsFinite(t.Start) && double.IsFinite(t.End) && t.End > t.Start)
                end += t.End - Math.Max(0, t.Start);
            bottom = Math.Max(bottom, end);
        }
        double motion = 0;
        foreach (var (el, _, _) in Walk())
            if (el.Track is { } t && double.IsFinite(t.Start) && double.IsFinite(t.End) && t.End > t.Start)
                motion = Math.Max(motion, t.End);
        return Math.Min(DesignCatalog.MaxPageHeight, Math.Max(Math.Max(0, bottom - DesignCanvas.ReferenceViewport), motion));
    }

    /// <summary>Every element, depth-first, with the group it sits in.</summary>
    public IEnumerable<(DesignElement Element, DesignElement? Parent, int Depth)> Walk()
    {
        var stack = new Stack<(DesignElement, DesignElement?, int)>();
        for (var i = Elements.Count - 1; i >= 0; i--) stack.Push((Elements[i], null, 0));
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            yield return item;
            var children = item.Item1.Children;
            if (children is null) continue;
            for (var i = children.Count - 1; i >= 0; i--) stack.Push((children[i], item.Item1, item.Item3 + 1));
        }
    }
}

public sealed class DesignSceneException(string message) : Exception(message);

public sealed class DesignCanvas
{
    public const double Width = 390;

    /// <summary>The phone the timeline is authored against. The page's scroll range is measured with it.</summary>
    public const double ReferenceViewport = 844;

    /// <summary>Schema 2 only: the screens a page used to be made of. Read, converted, never written.</summary>
    [JsonPropertyName("sections")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<DesignSection>? Sections { get; set; }
}

/// <summary>Schema 2's screen. Only read, to convert older designs.</summary>
public sealed class DesignSection
{
    [JsonPropertyName("id")] public string Id { get; set; } = default!;
    [JsonPropertyName("name")] public string Name { get; set; } = "Section";
    [JsonPropertyName("height")] public double Height { get; set; } = DesignCanvas.ReferenceViewport;
    /// <summary>A colour reference (see <see cref="DesignColor"/>), or null for the page background.</summary>
    [JsonPropertyName("background")] public string? Background { get; set; }
}

public sealed class DesignThemeEntry
{
    /// <summary>Emitted as <c>--ib-{key}</c>. A key containing <c>font</c> is a font; anything else a colour.</summary>
    [JsonPropertyName("key")] public string Key { get; set; } = default!;
    [JsonPropertyName("label")] public string Label { get; set; } = default!;
    /// <summary>A hex colour, or a font id for a font key.</summary>
    [JsonPropertyName("value")] public string Value { get; set; } = default!;

    [JsonIgnore] public bool IsFont => Key.Contains("font", StringComparison.Ordinal);
}

public sealed class DesignCustomField
{
    /// <summary>Always <c>event.{slug}</c>.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = default!;
    [JsonPropertyName("label")] public string Label { get; set; } = default!;
    /// <summary>text | textarea | date | time | url | color | select.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = "text";
    [JsonPropertyName("options")] public List<string>? Options { get; set; }
    [JsonPropertyName("roleScope")] public string? RoleScope { get; set; }
    /// <summary>What the editor shows in place of a real value.</summary>
    [JsonPropertyName("sample")] public string? Sample { get; set; }
}

public sealed class DesignAsset
{
    /// <summary>svg | image.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = default!;
    /// <summary>For svg: the sanitised markup. For image: a <c>data:image/…;base64,</c> URI.</summary>
    [JsonPropertyName("data")] public string Data { get; set; } = default!;
    [JsonPropertyName("width")] public double Width { get; set; }
    [JsonPropertyName("height")] public double Height { get; set; }
    /// <summary>Recolourable colours found in an SVG, in order: <c>--c0</c>, <c>--c1</c>, …</summary>
    [JsonPropertyName("colors")] public List<string>? Colors { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

public sealed class DesignElement
{
    [JsonPropertyName("id")] public string Id { get; set; } = default!;
    /// <summary>text | shape | svg | image | slot | rsvp | link | dress | group.</summary>
    [JsonPropertyName("type")] public string Type { get; set; } = default!;
    [JsonPropertyName("name")] public string? Name { get; set; }

    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("w")] public double W { get; set; } = 100;
    [JsonPropertyName("h")] public double H { get; set; } = 40;
    [JsonPropertyName("rotate")] public double Rotate { get; set; }
    [JsonPropertyName("scale")] public double Scale { get; set; } = 1;
    [JsonPropertyName("opacity")] public double Opacity { get; set; } = 1;

    /// <summary>The scroll range this element's motion plays over, or holds pinned for. Null means the whole page.</summary>
    [JsonPropertyName("track")] public DesignTrack? Track { get; set; }
    [JsonPropertyName("keyframes")] public List<DesignKeyframe> Keyframes { get; set; } = new();
    /// <summary>Preset ids that were applied; their keyframes carry the matching <see cref="DesignKeyframe.Preset"/> tag.</summary>
    [JsonPropertyName("enter")] public string? Enter { get; set; }
    [JsonPropertyName("exit")] public string? Exit { get; set; }
    /// <summary>Stays on screen for the length of its track, then scrolls away with the page.</summary>
    [JsonPropertyName("pinned")] public bool Pinned { get; set; }

    /// <summary>Shown only to guests whose role rules include this block id.</summary>
    [JsonPropertyName("block")] public string? Block { get; set; }
    /// <summary>The role that fills this element's fields, when they aren't shared.</summary>
    [JsonPropertyName("roleScope")] public string? RoleScope { get; set; }

    // ----- type-specific -----
    [JsonPropertyName("text")] public DesignText? Text { get; set; }
    [JsonPropertyName("shape")] public DesignShape? Shape { get; set; }
    [JsonPropertyName("svg")] public DesignSvg? Svg { get; set; }
    [JsonPropertyName("image")] public DesignImage? Image { get; set; }
    [JsonPropertyName("slot")] public DesignSlot? Slot { get; set; }
    [JsonPropertyName("button")] public DesignButton? Button { get; set; }
    [JsonPropertyName("dress")] public DesignDress? Dress { get; set; }
    [JsonPropertyName("children")] public List<DesignElement>? Children { get; set; }

    /// <summary>Editor-only: not emitted, never affects output.</summary>
    [JsonPropertyName("locked")] public bool Locked { get; set; }
}

public sealed class DesignTrack
{
    [JsonPropertyName("start")] public double Start { get; set; }
    [JsonPropertyName("end")] public double End { get; set; }
}

public sealed class DesignKeyframe
{
    /// <summary>Position within the track: 0 is its start, 1 its end.</summary>
    [JsonPropertyName("t")] public double T { get; set; }
    [JsonPropertyName("x")] public double? X { get; set; }
    [JsonPropertyName("y")] public double? Y { get; set; }
    [JsonPropertyName("rotate")] public double? Rotate { get; set; }
    [JsonPropertyName("scale")] public double? Scale { get; set; }
    [JsonPropertyName("opacity")] public double? Opacity { get; set; }
    /// <summary>
    /// How far in front of its neighbours it comes, 0–99. Animated like the rest, so a print swinging
    /// up passes over the ones beside it and drops back behind them after.
    /// </summary>
    [JsonPropertyName("lift")] public int? Lift { get; set; }
    /// <summary>Easing of the segment that STARTS at this keyframe. CSS ignores it on the last one.</summary>
    [JsonPropertyName("easing")] public string? Easing { get; set; }
    /// <summary>enter | exit when a preset created it.</summary>
    [JsonPropertyName("preset")] public string? Preset { get; set; }
}

/// <summary>One run of a text element: literal text, or a bound variable.</summary>
public sealed class DesignRun
{
    [JsonPropertyName("text")] public string? Text { get; set; }
    [JsonPropertyName("var")] public string? Var { get; set; }
    [JsonPropertyName("bold")] public bool Bold { get; set; }
    [JsonPropertyName("italic")] public bool Italic { get; set; }
}

public sealed class DesignTypography
{
    /// <summary>A theme font key (<c>theme:heading-font</c>) or a catalog font id.</summary>
    [JsonPropertyName("font")] public string? Font { get; set; }
    [JsonPropertyName("size")] public double Size { get; set; } = 18;
    [JsonPropertyName("weight")] public int Weight { get; set; } = 400;
    [JsonPropertyName("italic")] public bool Italic { get; set; }
    [JsonPropertyName("color")] public string? Color { get; set; }
    /// <summary>left | center | right.</summary>
    [JsonPropertyName("align")] public string Align { get; set; } = "center";
    /// <summary>top | middle | bottom.</summary>
    [JsonPropertyName("valign")] public string VAlign { get; set; } = "middle";
    [JsonPropertyName("lineHeight")] public double LineHeight { get; set; } = 1.3;
    /// <summary>In em.</summary>
    [JsonPropertyName("letterSpacing")] public double LetterSpacing { get; set; }
    [JsonPropertyName("uppercase")] public bool Uppercase { get; set; }
}

public sealed class DesignText
{
    [JsonPropertyName("runs")] public List<DesignRun> Runs { get; set; } = new();
    [JsonPropertyName("style")] public DesignTypography Style { get; set; } = new();
}

public sealed class DesignShape
{
    /// <summary>rect | ellipse | line | polygon | path.</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = "rect";
    /// <summary>The outline of a <c>path</c> shape, drawn in the shape editor.</summary>
    [JsonPropertyName("path")] public DesignPath? Path { get; set; }
    [JsonPropertyName("sides")] public int Sides { get; set; } = 6;
    [JsonPropertyName("fill")] public string? Fill { get; set; }
    [JsonPropertyName("stroke")] public string? Stroke { get; set; }
    [JsonPropertyName("strokeWidth")] public double StrokeWidth { get; set; }
    [JsonPropertyName("radius")] public double Radius { get; set; }
}

/// <summary>
/// A drawn outline: contours of points with optional Bézier handles, in a space of <see cref="Width"/> ×
/// <see cref="Height"/> units that is stretched onto the element's box.
/// </summary>
public sealed class DesignPath
{
    [JsonPropertyName("width")] public double Width { get; set; } = 100;
    [JsonPropertyName("height")] public double Height { get; set; } = 100;
    [JsonPropertyName("contours")] public List<DesignContour> Contours { get; set; } = new();
}

public sealed class DesignContour
{
    [JsonPropertyName("closed")] public bool Closed { get; set; } = true;
    [JsonPropertyName("points")] public List<DesignPathPoint> Points { get; set; } = new();
}

public sealed class DesignPathPoint
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    /// <summary>Curve handle toward the previous point; absolute, in path units.</summary>
    [JsonPropertyName("in")] public DesignXY? In { get; set; }
    /// <summary>Curve handle toward the next point.</summary>
    [JsonPropertyName("out")] public DesignXY? Out { get; set; }
}

public sealed class DesignXY
{
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
}

public sealed class DesignSvg
{
    [JsonPropertyName("asset")] public string Asset { get; set; } = default!;
    /// <summary>Original colour (as listed on the asset) → colour reference to paint it with.</summary>
    [JsonPropertyName("fills")] public Dictionary<string, string> Fills { get; set; } = new();
}

public sealed class DesignImage
{
    [JsonPropertyName("asset")] public string Asset { get; set; } = default!;
    /// <summary>cover | contain.</summary>
    [JsonPropertyName("fit")] public string Fit { get; set; } = "cover";
    [JsonPropertyName("radius")] public double Radius { get; set; }
}

/// <summary>An image the inviter uploads in the builder — a <c>data-src</c> slot.</summary>
public sealed class DesignSlot
{
    /// <summary>Always <c>event.{slug}</c>.</summary>
    [JsonPropertyName("path")] public string Path { get; set; } = "event.coverImage";
    [JsonPropertyName("label")] public string Label { get; set; } = "Photo";
    [JsonPropertyName("fit")] public string Fit { get; set; } = "cover";
    [JsonPropertyName("radius")] public double Radius { get; set; }
    [JsonPropertyName("multiple")] public bool Multiple { get; set; }
    [JsonPropertyName("min")] public int? Min { get; set; }
    [JsonPropertyName("max")] public int? Max { get; set; }
    /// <summary>Columns for a gallery.</summary>
    [JsonPropertyName("columns")] public int Columns { get; set; } = 2;
    [JsonPropertyName("gap")] public double Gap { get; set; } = 8;
    /// <summary>Width ÷ height of each gallery print.</summary>
    [JsonPropertyName("aspect")] public double Aspect { get; set; } = 1;
    /// <summary>
    /// One photo out of a gallery: 1 is its first. The host still fills one gallery; each element shows
    /// the photo at its place, and hides when the gallery has fewer.
    /// </summary>
    [JsonPropertyName("index")] public int? Index { get; set; }
}

/// <summary>An RSVP button, or a link to a platform page (camera, photos, map) or a URL field.</summary>
public sealed class DesignButton
{
    /// <summary>For links: <c>camera.link</c>, <c>photos.link</c>, <c>event.venue.mapLink</c> or an <c>event.*</c> url field. Ignored for RSVP.</summary>
    [JsonPropertyName("path")] public string? Path { get; set; }
    /// <summary>What the button says. RSVP wording comes from the platform and ignores this.</summary>
    [JsonPropertyName("label")] public string Label { get; set; } = "Open";
    [JsonPropertyName("fill")] public string? Fill { get; set; }
    [JsonPropertyName("stroke")] public string? Stroke { get; set; }
    [JsonPropertyName("strokeWidth")] public double StrokeWidth { get; set; }
    [JsonPropertyName("radius")] public double Radius { get; set; } = 999;
    [JsonPropertyName("style")] public DesignTypography Style { get; set; } = new();
}

public sealed class DesignDress
{
    [JsonPropertyName("swatch")] public double Swatch { get; set; } = 40;
    /// <summary>circle | square.</summary>
    [JsonPropertyName("shape")] public string Shape { get; set; } = "circle";
    [JsonPropertyName("gap")] public double Gap { get; set; } = 10;
    [JsonPropertyName("style")] public DesignTypography Style { get; set; } = new();
}
