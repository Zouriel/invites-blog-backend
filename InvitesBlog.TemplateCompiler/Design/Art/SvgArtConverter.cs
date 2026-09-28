using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace InvitesBlog.TemplateCompiler.Design.Art;

/// <summary>One keyframe of a layer's motion: where it is at <see cref="T"/> of the scroll track, relative to rest.</summary>
/// <param name="T">0–1 through the track.</param>
/// <param name="Dx">Offset in art units (the SVG's viewBox units).</param>
/// <param name="Rotate">Degrees, about the art's centre.</param>
/// <param name="Scale">Uniform scale, about the art's centre.</param>
public sealed record ArtFrame(double T, double Dx, double Dy, double Rotate, double Scale, double Opacity);

/// <summary>One stacked piece of the art: a still SVG, and how it moves (empty when it doesn't).</summary>
public sealed record ArtLayerPlan(string Svg, string? Name, IReadOnlyList<ArtFrame> Frames);

/// <summary>
/// An illustration made ready for the designer: layers back to front, each covering the whole art box.
/// </summary>
/// <param name="Seconds">How long the original animation ran for what the track now covers.</param>
/// <param name="Loops">How many times a repeating animation plays over the track.</param>
public sealed record ArtPlan(double Width, double Height, IReadOnlyList<ArtLayerPlan> Layers, bool Animated, double Seconds, int Loops);

public sealed class ArtRejectedException(string message) : Exception(message);

/// <summary>
/// Turns an animated SVG into what the designer can play: <b>scroll</b> motion instead of time.
///
/// <para>The designer's elements move by keyframes of position, rotation, uniform scale and opacity,
/// played against scroll. So the SVG is cut, in paint order, into layers that each move as one — the
/// still parts in between become still layers, which keeps what's in front in front. Every layer keeps
/// the whole art's viewBox, so the layers stack exactly and a layer's motion is a transform of the art
/// box: sampled from the original animation over time, then simplified to the keyframe limit.</para>
///
/// <para>What moves that way: SMIL <c>animateTransform</c>, <c>animateMotion</c>, and <c>animate</c>/<c>set</c>
/// of opacity, visibility, display and x/y/cx/cy; CSS <c>@keyframes</c> of transform and opacity.
/// Anything else — colours, path morphs, dash offsets — can't be a transform, so that part becomes a
/// <b>flipbook</b>: a few still copies at moments of the animation, each shown in turn as you scroll.
/// When the animation lives somewhere a layer can't reach (a symbol reused elsewhere, a gradient), or
/// there'd be too many layers, the whole picture becomes a flipbook instead.</para>
///
/// <para>Output SVG is NOT safe yet — it is animation-free, and every layer still goes through
/// <see cref="SvgSanitizer"/> before it is used.</para>
/// </summary>
public static partial class SvgArtConverter
{
    public const int MaxLayers = 16;
    public const int MaxFlipFrames = 10;
    public const double MaxSeconds = 30;
    /// <summary>Elements per layer, under the sanitiser's node limit with room for text nodes.</summary>
    public const int MaxLayerNodes = 6000;
    private const double Epsilon = 0.0015;
    private const string Marker = "data-art-k";

    private static readonly HashSet<string> AnimationTags = new(StringComparer.Ordinal)
        { "animate", "animateTransform", "animateMotion", "set", "animateColor" };

    private static readonly HashSet<string> Resources = new(StringComparer.Ordinal)
    {
        "defs", "symbol", "clipPath", "mask", "pattern", "linearGradient", "radialGradient", "filter", "marker",
    };

    private static readonly HashSet<string> Ignored = new(StringComparer.Ordinal)
        { "style", "title", "desc", "metadata", "script", "foreignObject", "image" };

    private static readonly HashSet<string> Leaves = new(StringComparer.Ordinal)
        { "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "text", "use" };

    /// <summary>Attributes a flipbook frame writes as attributes; everything else goes into <c>style</c>.</summary>
    private static readonly HashSet<string> GeometryAttributes = new(StringComparer.Ordinal)
    {
        "d", "points", "x", "y", "cx", "cy", "r", "rx", "ry", "width", "height", "x1", "y1", "x2", "y2", "offset",
        "stdDeviation", "dx", "dy", "fx", "fy", "viewBox", "values", "href",
    };

    private static readonly HashSet<string> CompactAttributes = new(StringComparer.Ordinal)
    {
        "d", "points", "transform", "x", "y", "cx", "cy", "r", "rx", "ry", "width", "height", "x1", "y1", "x2", "y2",
        "stroke-width", "gradientTransform", "patternTransform", "fx", "fy",
    };

    /// <param name="coarser">Decimal places to drop below the usual precision, for art that's too heavy otherwise.</param>
    /// <param name="maxLayerBytes">A layer bigger than this (or with too many elements for the sanitiser) is cut
    /// into several, in paint order, which stack back into the same picture.</param>
    public static ArtPlan Convert(string markup, int maxFlipFrames = 8, int coarser = 0, int maxLayerBytes = int.MaxValue)
    {
        var doc = Load(markup);
        var root = doc.Root!;
        var box = ViewBox(root);
        var art = new Art(root, box);
        art.Prepare();

        var decimals = Math.Max(0, (box.W >= 2000 || box.H >= 2000 ? 0 : box.W >= 200 || box.H >= 200 ? 1 : box.W >= 20 || box.H >= 20 ? 2 : 3) - coarser);
        maxFlipFrames = Math.Clamp(maxFlipFrames, 2, MaxFlipFrames);

        // Renders a segment, cutting it in two (by weight, keeping paint order) while it's too big.
        void Emit(Segment segment, string? name, IReadOnlyList<ArtFrame> frames, List<ArtLayerPlan> into)
        {
            var svg = art.Layer(segment, decimals);
            if ((Encoding.UTF8.GetByteCount(svg) > maxLayerBytes || svg.Count(c => c == '<') > MaxLayerNodes) && segment.Leaves.Count > 1)
            {
                var weights = segment.Leaves.Select(l => (double)l.ToString(SaveOptions.DisableFormatting).Length).ToList();
                var half = weights.Sum() / 2;
                var cut = 1;
                for (double run = weights[0]; cut < weights.Count - 1 && run + weights[cut] <= half; cut++) run += weights[cut];
                Emit(new Segment { Owner = segment.Owner, Leaves = segment.Leaves[..cut] }, name, frames, into);
                Emit(new Segment { Owner = segment.Owner, Leaves = segment.Leaves[cut..] }, name, frames, into);
                return;
            }
            into.Add(new ArtLayerPlan(svg, name, frames));
        }

        var timeline = art.Timeline();
        if (timeline is null)
        {
            var still = new List<ArtLayerPlan>();
            if (art.Segments() is { Count: > 0 } parts)
                foreach (var part in parts) Emit(part, null, [], still);
            else still.Add(new ArtLayerPlan(art.Layer(null, decimals), null, []));
            return new ArtPlan(box.W, box.H, still, false, 0, 1);
        }

        var (seconds, loops) = timeline.Value;
        var segments = art.Segments();
        if (segments is null || segments.Count > MaxLayers)
            return new ArtPlan(box.W, box.H, art.Flipbook(null, seconds, loops, maxFlipFrames, decimals, "Frame"), true, seconds, loops);

        var layers = new List<ArtLayerPlan>();
        var flipbooks = segments.Count(s => s.Owner is not null && !art.Moves(s.Owner));
        var framesEach = flipbooks == 0 ? maxFlipFrames : Math.Max(2, Math.Min(maxFlipFrames, MaxLayers * 2 / Math.Max(1, flipbooks)));
        foreach (var segment in segments)
        {
            if (segment.Owner is null)
                Emit(segment, null, [], layers);
            else if (art.Moves(segment.Owner))
                Emit(segment, art.NameOf(segment.Owner), art.Motion(segment, seconds), layers);
            else
                layers.AddRange(art.Flipbook(segment, seconds, loops, framesEach, decimals, art.NameOf(segment.Owner)));
        }
        return new ArtPlan(box.W, box.H, layers, true, seconds, loops);
    }

    private static XDocument Load(string markup)
    {
        if (string.IsNullOrWhiteSpace(markup)) throw new ArtRejectedException("The SVG is empty.");
        markup = WithoutDoctype(markup);
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 8_000_000,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(new StringReader(markup), settings);
            var doc = XDocument.Load(reader);
            if (doc.Root is null || doc.Root.Name.LocalName != "svg") throw new ArtRejectedException("That file isn't an SVG.");
            return doc;
        }
        catch (XmlException e)
        {
            throw new ArtRejectedException($"That file isn't a valid SVG ({e.Message}).");
        }
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"<!DOCTYPE\s+svg\b[^\[>]*(?:\[(?<subset>[^\]]*)\])?\s*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex DoctypeRegex();

    [System.Text.RegularExpressions.GeneratedRegex(@"<!ENTITY\s+([A-Za-z_][\w.-]*)\s+""([^""&<%]*)""\s*>")]
    private static partial System.Text.RegularExpressions.Regex EntityRegex();

    /// <summary>
    /// Takes off the DOCTYPE that Inkscape and Illustrator write, so the document can be read with DTDs
    /// still prohibited. Illustrator's internal subset declares namespace entities
    /// (<c>&lt;!ENTITY ns_svg "http://..."&gt;</c>); those are expanded when they are plain text — a value
    /// with no <c>&amp;</c> can't expand into more entities, so there's no way to build a bomb from them.
    /// Anything else in the subset (external or parameter entities) leaves the DOCTYPE on, and the
    /// parser refuses the file.
    /// </summary>
    internal static string WithoutDoctype(string markup)
    {
        var m = DoctypeRegex().Match(markup);
        if (!m.Success) return markup;
        var entities = new Dictionary<string, string>(StringComparer.Ordinal);
        if (m.Groups["subset"].Success)
        {
            var subset = m.Groups["subset"].Value;
            var matches = EntityRegex().Matches(subset);
            var rest = EntityRegex().Replace(subset, "").Trim();
            if (rest.Length > 0 || matches.Count > 64) return markup;
            foreach (System.Text.RegularExpressions.Match e in matches)
                if (e.Groups[2].Value.Length <= 2000) entities[e.Groups[1].Value] = e.Groups[2].Value;
                else return markup;
        }
        var body = markup.Remove(m.Index, m.Length);
        foreach (var (name, value) in entities)
            body = body.Replace($"&{name};", System.Security.SecurityElement.Escape(value), StringComparison.Ordinal);
        return body;
    }

    internal readonly record struct Box(double X, double Y, double W, double H);

    private static Box ViewBox(XElement root)
    {
        var nums = Affine.Numbers((string?)root.Attribute("viewBox"));
        if (nums.Count == 4 && nums[2] > 0 && nums[3] > 0) return new Box(nums[0], nums[1], nums[2], nums[3]);
        double Len(string name) => ArtValues.Number((string?)root.Attribute(name)) is { } v && v > 0 && !((string?)root.Attribute(name))!.Contains('%') ? v : 100;
        return new Box(0, 0, Len("width"), Len("height"));
    }

    // ================================================================================================
    // Timing
    // ================================================================================================

    /// <summary>When an animation is in effect, and how far through its current run it is.</summary>
    private sealed class Timing
    {
        public double Begin;
        public double Dur = double.PositiveInfinity;
        public double Count = 1;
        public double RepeatDur = double.NaN;
        public string Direction = "normal";
        public bool Backwards;
        public bool Forwards;

        public double ActiveDuration
        {
            get
            {
                var d = double.IsInfinity(Dur) || double.IsInfinity(Count) ? double.PositiveInfinity : Dur * Count;
                return double.IsNaN(RepeatDur) ? d : Math.Min(d, RepeatDur);
            }
        }

        public bool Infinite => double.IsInfinity(ActiveDuration);
        public double Period => double.IsInfinity(Dur) ? 0 : Dur * (Direction.StartsWith("alternate", StringComparison.Ordinal) ? 2 : 1);

        /// <summary>0–1 through the current iteration at <paramref name="t"/>, or null when not in effect.</summary>
        public double? Progress(double t)
        {
            var local = t - Begin;
            if (local < 0) return Backwards ? Directed(0, 0) : null;
            var active = ActiveDuration;
            if (local >= active)
            {
                if (!Forwards) return null;
                if (double.IsInfinity(Dur) || Dur <= 0) return 0;
                var iterations = active / Dur;
                var whole = Math.Floor(iterations);
                return iterations - whole < 1e-9 && whole > 0 ? Directed(whole - 1, 1) : Directed(whole, iterations - whole);
            }
            if (double.IsInfinity(Dur) || Dur <= 0) return 0;
            var iter = Math.Floor(local / Dur);
            return Directed(iter, local / Dur - iter);
        }

        private double Directed(double iteration, double p)
        {
            var odd = ((long)iteration & 1) == 1;
            return Direction switch
            {
                "reverse" => 1 - p,
                "alternate" => odd ? 1 - p : p,
                "alternate-reverse" => odd ? p : 1 - p,
                _ => p,
            };
        }
    }

    // ================================================================================================
    // Animations
    // ================================================================================================

    private abstract class Anim
    {
        public required XElement Target;
        public required Timing Timing;
        public int Order;
        /// <summary>The SMIL element it was read from (null for CSS).</summary>
        public XElement? Source;
        /// <summary>What it changes: an attribute or CSS property, or "transform" / "motion".</summary>
        public abstract IEnumerable<string> Properties { get; }
    }

    /// <summary>Values at key times, blended in between.</summary>
    private sealed class KeyedValues
    {
        public List<string> Values = new();
        public List<double> Times = new();
        public List<Easing?> Easings = new();
        public bool Discrete;

        public string? At(double p)
        {
            if (Values.Count == 0) return null;
            if (Values.Count == 1) return Values[0];
            if (Discrete)
            {
                var idx = 0;
                for (var k = 0; k < Times.Count; k++) if (p >= Times[k]) idx = k;
                return Values[Math.Min(idx, Values.Count - 1)];
            }
            if (p <= Times[0]) return Values[0];
            for (var k = 0; k < Values.Count - 1; k++)
            {
                if (p > Times[k + 1] && k + 1 < Values.Count - 1) continue;
                var span = Times[k + 1] - Times[k];
                var local = span <= 0 ? 1 : Math.Clamp((p - Times[k]) / span, 0, 1);
                var eased = Easings.Count > k && Easings[k] is { } e ? e.Apply(local) : local;
                return ArtValues.Blend(Values[k], Values[k + 1], eased);
            }
            return Values[^1];
        }
    }

    private sealed class SmilAttribute : Anim
    {
        public required string Attribute;
        public required KeyedValues Values;
        public override IEnumerable<string> Properties => [Attribute];
    }

    private sealed class SmilTransform : Anim
    {
        public required string Type;
        public required KeyedValues Values;
        public bool Additive;
        public override IEnumerable<string> Properties => ["transform"];
        public Affine At(double p) => Affine.Function(Type, Affine.Numbers(Values.At(p)));
    }

    private sealed class SmilMotion : Anim
    {
        public required PathGeometry Path;
        public List<double>? KeyPoints;
        public List<double>? KeyTimes;
        public string Rotate = "0";
        public override IEnumerable<string> Properties => ["motion"];

        public Affine At(double p)
        {
            var fraction = p;
            if (KeyPoints is { Count: > 1 } kp && KeyTimes is { Count: var n } kt && n == kp.Count)
            {
                fraction = kp[^1];
                for (var k = 0; k < n - 1; k++)
                    if (p <= kt[k + 1])
                    {
                        var span = kt[k + 1] - kt[k];
                        fraction = kp[k] + (kp[k + 1] - kp[k]) * (span <= 0 ? 1 : Math.Clamp((p - kt[k]) / span, 0, 1));
                        break;
                    }
            }
            var (x, y, deg) = Path.At(fraction);
            var angle = Rotate switch
            {
                "auto" => deg,
                "auto-reverse" => deg + 180,
                _ => ArtValues.Number(Rotate) ?? 0,
            };
            return Affine.Translate(x, y) * Affine.Rotate(angle);
        }
    }

    private sealed class CssAnim : Anim
    {
        public required List<MiniCss.KeyframeStop> Stops;
        public required Easing Easing;
        public required Func<string, string?> Static;
        /// <summary>A transform value as a matrix, with the element's origin and reference box applied.</summary>
        public required Func<string, Affine> Matrix;
        public override IEnumerable<string> Properties =>
            Stops.SelectMany(s => s.Declarations.Keys).Where(k => k != "animation-timing-function").Distinct();

        /// <summary>A property's value at a progress, with CSS's implicit 0% and 100% from the element's own value.</summary>
        public string? Value(string property, double p)
        {
            var stops = Stops.Where(s => s.Declarations.ContainsKey(property)).Select(s => (s.Offset, Value: s.Declarations[property], s)).ToList();
            if (stops.Count == 0) return null;
            var start = Static(property) ?? (property == "transform" ? "none" : null);
            if (stops[0].Offset > 0 && start is not null) stops.Insert(0, (0, start, null!));
            if (stops[^1].Offset < 1 && start is not null) stops.Add((1, start, null!));
            if (p <= stops[0].Offset) return stops[0].Value;
            for (var k = 0; k < stops.Count - 1; k++)
            {
                if (p > stops[k + 1].Offset) continue;
                var span = stops[k + 1].Offset - stops[k].Offset;
                var local = span <= 0 ? 1 : (p - stops[k].Offset) / span;
                var easing = stops[k].s?.Declarations.TryGetValue("animation-timing-function", out var tf) == true ? Easing.Parse(tf) : Easing;
                var eased = easing.Apply(local);
                return property == "transform"
                    ? BlendTransform(stops[k].Value, stops[k + 1].Value, eased)
                    : ArtValues.Blend(stops[k].Value, stops[k + 1].Value, eased);
            }
            return stops[^1].Value;
        }

        public Affine? Transform(double p)
        {
            var v = Value("transform", p);
            return v is null ? null : Matrix(v);
        }

        /// <summary>Transforms between two stops: function by function when they line up, the switch halfway when not.</summary>
        private static string BlendTransform(string a, string b, double p)
        {
            var fa = CssTransform.Canonical(a);
            var fb = CssTransform.Canonical(b);
            if (fa.Count == 0 && fb.Count > 0) fa = fb.Select(CssTransform.Neutral).ToList();
            if (fb.Count == 0 && fa.Count > 0) fb = fa.Select(CssTransform.Neutral).ToList();
            var joinedA = string.Join(' ', fa);
            var joinedB = string.Join(' ', fb);
            // Same functions with the same units: ArtValues.Blend moves every number between them.
            return fa.Count == fb.Count ? ArtValues.Blend(joinedA, joinedB, p) : p < 0.5 ? a : b;
        }
    }

    // ================================================================================================
    // The art
    // ================================================================================================

    internal sealed class Segment
    {
        public XElement? Owner;
        public List<XElement> Leaves = new();
    }

    private sealed partial class Art(XElement root, Box box)
    {
        private readonly List<Anim> anims = new();
        private readonly Dictionary<XElement, List<Anim>> byTarget = new();
        private readonly Dictionary<XElement, Affine> staticTransform = new();
        private readonly Dictionary<XElement, Dictionary<string, string>> staticStyle = new();
        private readonly Dictionary<string, XElement> byId = new(StringComparer.Ordinal);
        private readonly Dictionary<XElement, int> keys = new();
        private readonly Dictionary<int, XElement> byKey = new();
        private MiniCss sheet = new();

        public void Prepare()
        {
            var k = 0;
            foreach (var a in root.Descendants().Where(e => e.Name.LocalName is "a" or "switch").ToList())
            {
                a.Name = a.Name.Namespace + "g";
                a.Attributes().Where(x => x.Name.LocalName == "href").Remove();
            }
            foreach (var el in root.DescendantsAndSelf())
            {
                keys[el] = k;
                byKey[k] = el;
                el.SetAttributeValue(Marker, k++);
                if ((string?)el.Attribute("id") is { Length: > 0 } id) byId.TryAdd(id, el);
            }
            if (k > 100_000) throw new ArtRejectedException("That SVG is too complex.");

            sheet = MiniCss.Parse(string.Join('\n', root.Descendants().Where(e => e.Name.LocalName == "style").Select(e => e.Value)));
            InlineStyles();
            ReadSmil();
        }

        public string NameOf(XElement el) => (string?)el.Attribute("id") is { Length: > 0 and <= 40 } id ? id : el.Name.LocalName;

        // ----- Styles -------------------------------------------------------------------------------

        /// <summary>
        /// Folds the stylesheet into each element's <c>style</c> — the sanitiser drops <c>&lt;style&gt;</c>, and
        /// with it every colour an illustrator's export put in classes. Static CSS transforms become the
        /// transform attribute; CSS animations are read into <see cref="CssAnim"/>s.
        /// </summary>
        private void InlineStyles()
        {
            var order = 0;
            foreach (var el in root.DescendantsAndSelf().ToList())
            {
                if (el.Name.LocalName == "style" || AnimationTags.Contains(el.Name.LocalName)) continue;
                var decls = sheet.Cascade(el);
                var style = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var (name, value) in decls) style[name] = value;
                staticStyle[el] = style;

                var animation = Animations(style);
                var origin = style.GetValueOrDefault("transform-origin");
                var fillBox = style.GetValueOrDefault("transform-box") is "fill-box" or "content-box" or "border-box";
                Affine Matrix(List<(string Name, double[] Args)> fns) => OriginMatrix(el, origin, fillBox, fns);

                if (style.TryGetValue("transform", out var t) && t != "none")
                {
                    var refBox = fillBox ? Bounds(el) : null;
                    el.SetAttributeValue("transform", Matrix(CssTransform.Parse(t, refBox?.W ?? box.W, refBox?.H ?? box.H)).ToSvg());
                }
                else if (style.ContainsKey("transform")) el.SetAttributeValue("transform", null);
                staticTransform[el] = el == root ? Affine.Identity : Affine.Parse((string?)el.Attribute("transform"));

                // Write back what's left, minus what the page can't use.
                var keep = style.Where(p => !p.Key.StartsWith("animation", StringComparison.Ordinal)
                                            && p.Key is not ("transform" or "transform-origin" or "transform-box" or "transition" or "will-change"))
                    .Select(p => $"{p.Key}:{p.Value}");
                var joined = string.Join(';', keep);
                el.SetAttributeValue("style", joined.Length > 0 ? joined : null);

                foreach (var a in animation)
                {
                    if (!sheet.Keyframes.TryGetValue(a.Name, out var stops) || stops.Count == 0) continue;
                    var target = el;
                    anims.Add(new CssAnim
                    {
                        Target = target, Timing = a.Timing, Order = 100_000 + order++, Stops = stops, Easing = a.Easing,
                        Static = prop => StaticValue(target, prop),
                        Matrix = value =>
                        {
                            var refBox = fillBox ? Bounds(target) : null;
                            return Matrix(CssTransform.Parse(value, refBox?.W ?? box.W, refBox?.H ?? box.H));
                        },
                    });
                }
            }
            foreach (var a in anims) Track(a);
        }

        private Affine OriginMatrix(XElement el, string? origin, bool fillBox, List<(string Name, double[] Args)> fns)
        {
            var refBox = fillBox ? Bounds(el) : null;
            var (rx, ry, rw, rh) = refBox is { } b ? (b.X, b.Y, b.W, b.H) : (0.0, 0.0, box.W, box.H);
            var (ox, oy) = CssTransform.Origin(origin, rx, ry, rw, rh, fillBox);
            var m = Affine.Identity;
            foreach (var (name, args) in fns) m *= CssTransform.Function(name, args);
            return ox == 0 && oy == 0 ? m : Affine.Translate(ox, oy) * m * Affine.Translate(-ox, -oy);
        }

        private string? StaticValue(XElement el, string property)
        {
            if (staticStyle.TryGetValue(el, out var s) && s.TryGetValue(property, out var v)) return v;
            if (property == "transform") return null;
            return (string?)el.Attribute(property);
        }

        private readonly record struct CssAnimation(string Name, Timing Timing, Easing Easing);

        /// <summary>The animations a style asks for (the shorthand, then any longhands over it).</summary>
        private static List<CssAnimation> Animations(Dictionary<string, string> style)
        {
            var list = new List<(string Name, Timing Timing, string Easing)>();
            if (style.TryGetValue("animation", out var shorthand))
                foreach (var one in MiniCss.SplitTopLevel(shorthand, ','))
                    list.Add(Shorthand(one));
            if (style.TryGetValue("animation-name", out var names))
            {
                var parts = MiniCss.SplitTopLevel(names, ',');
                while (list.Count < parts.Count) list.Add(("", new Timing { Dur = 0 }, "ease"));
                for (var k = 0; k < parts.Count; k++) list[k] = (parts[k].Trim(), list[k].Timing, list[k].Easing);
            }
            void Longhand(string prop, Action<Timing, string> apply)
            {
                if (!style.TryGetValue(prop, out var v)) return;
                var parts = MiniCss.SplitTopLevel(v, ',');
                for (var k = 0; k < list.Count && parts.Count > 0; k++) apply(list[k].Timing, parts[k % parts.Count]);
            }
            Longhand("animation-duration", (t, v) => t.Dur = ArtValues.Seconds(v) ?? 0);
            Longhand("animation-delay", (t, v) => t.Begin = ArtValues.Seconds(v) ?? 0);
            Longhand("animation-iteration-count", (t, v) => t.Count = v.Trim() == "infinite" ? double.PositiveInfinity : ArtValues.Number(v) ?? 1);
            Longhand("animation-direction", (t, v) => t.Direction = v.Trim());
            Longhand("animation-fill-mode", (t, v) => (t.Backwards, t.Forwards) = Fill(v.Trim()));
            if (style.TryGetValue("animation-timing-function", out var tf))
            {
                var parts = MiniCss.SplitTopLevel(tf, ',');
                for (var k = 0; k < list.Count && parts.Count > 0; k++) list[k] = (list[k].Name, list[k].Timing, parts[k % parts.Count]);
            }
            return list.Where(a => a.Name.Length > 0 && a.Name != "none" && a.Timing.Dur > 0)
                .Select(a => new CssAnimation(a.Name, a.Timing, Easing.Parse(a.Easing))).ToList();
        }

        private static (bool Backwards, bool Forwards) Fill(string v) => v switch
        {
            "forwards" => (false, true),
            "backwards" => (true, false),
            "both" => (true, true),
            _ => (false, false),
        };

        private static (string Name, Timing Timing, string Easing) Shorthand(string text)
        {
            var timing = new Timing { Dur = 0 };
            var easing = "ease";
            var name = "";
            var times = 0;
            foreach (var token in Tokens(text))
            {
                var t = token.ToLowerInvariant();
                if ((t.EndsWith("ms") || t.EndsWith('s')) && ArtValues.Seconds(t) is { } secs && t.TrimStart('-', '+', '.') is [var first, ..] && char.IsAsciiDigit(first))
                {
                    if (times++ == 0) timing.Dur = secs; else timing.Begin = secs;
                }
                else if (t is "linear" or "ease" or "ease-in" or "ease-out" or "ease-in-out" or "step-start" or "step-end"
                         || t.StartsWith("cubic-bezier(") || t.StartsWith("steps(")) easing = token;
                else if (t == "infinite") timing.Count = double.PositiveInfinity;
                else if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)) timing.Count = n;
                else if (t is "normal" or "reverse" or "alternate" or "alternate-reverse") timing.Direction = t;
                else if (t is "forwards" or "backwards" or "both" or "none") (timing.Backwards, timing.Forwards) = Fill(t);
                else if (t is "running" or "paused") { }
                else name = token;
            }
            return (name, timing, easing);
        }

        private static IEnumerable<string> Tokens(string text)
        {
            var depth = 0;
            var start = -1;
            for (var k = 0; k <= text.Length; k++)
            {
                var c = k < text.Length ? text[k] : ' ';
                if (c == '(') depth++;
                if (c == ')') depth--;
                if (char.IsWhiteSpace(c) && depth == 0)
                {
                    if (start >= 0) yield return text[start..k];
                    start = -1;
                }
                else if (start < 0) start = k;
            }
        }

        // ----- SMIL ---------------------------------------------------------------------------------

        private void ReadSmil()
        {
            var smil = root.Descendants().Where(e => AnimationTags.Contains(e.Name.LocalName)).ToList();
            if (smil.Count > 400) throw new ArtRejectedException("That SVG has too many animations to convert.");
            var begins = new Dictionary<XElement, (Timing Timing, string? Begin)>();
            var order = 0;
            foreach (var el in smil)
            {
                var target = Target(el);
                if (target is null) continue;
                var timing = new Timing
                {
                    Dur = (string?)el.Attribute("dur") is { } dur && dur.Trim() != "indefinite" && dur.Trim() != "media"
                        ? ArtValues.Seconds(dur) ?? double.PositiveInfinity : double.PositiveInfinity,
                    Forwards = (string?)el.Attribute("fill") == "freeze",
                };
                var repeat = ((string?)el.Attribute("repeatCount"))?.Trim();
                if (repeat == "indefinite") timing.Count = double.PositiveInfinity;
                else if (ArtValues.Number(repeat) is { } rc && rc > 0) timing.Count = rc;
                var repeatDur = ((string?)el.Attribute("repeatDur"))?.Trim();
                if (repeatDur == "indefinite") { timing.RepeatDur = double.PositiveInfinity; timing.Count = double.PositiveInfinity; }
                else if (ArtValues.Seconds(repeatDur) is { } rd && rd > 0)
                {
                    timing.RepeatDur = rd;
                    if ((string?)el.Attribute("repeatCount") is null) timing.Count = double.PositiveInfinity;
                }

                Anim? anim = el.Name.LocalName switch
                {
                    "animateTransform" => TransformAnim(el, target, timing),
                    "animateMotion" => MotionAnim(el, target, timing),
                    "set" => SetAnim(el, target, timing),
                    _ => AttributeAnim(el, target, timing),
                };
                if (anim is null) continue;
                anim.Order = order++;
                anim.Source = el;
                anims.Add(anim);
                begins[el] = (timing, (string?)el.Attribute("begin"));
            }

            // Begin times: offsets, and "other.begin/end ± offset" chains resolved from them. Anything
            // waiting on an event (a click, a hover) never starts here: scrolling can't click.
            var resolved = new Dictionary<XElement, double>();
            for (var pass = 0; pass < 8; pass++)
            {
                foreach (var (el, (timing, begin)) in begins)
                {
                    if (resolved.ContainsKey(el)) continue;
                    double? best = null;
                    foreach (var item in (begin ?? "0s").Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
                    {
                        double? at = null;
                        if (IsClock(item)) at = ArtValues.Seconds(item);
                        else if (Syncbase(item) is var (id, edge, delta) && byId.TryGetValue(id, out var other) && resolved.TryGetValue(other, out var otherBegin))
                        {
                            var ot = begins[other].Timing;
                            at = (edge == "end" ? otherBegin + ot.ActiveDuration : otherBegin) + delta;
                        }
                        if (at is { } a && double.IsFinite(a) && (best is null || a < best)) best = a;
                    }
                    if (best is { } b) { resolved[el] = b; timing.Begin = b; }
                }
            }
            anims.RemoveAll(a => a.Source is { } source && !resolved.ContainsKey(source));
        }

        private static bool IsClock(string item) =>
            item.Length > 0 && (char.IsAsciiDigit(item[0]) || item[0] is '-' or '+' or '.') && ArtValues.Seconds(item) is not null;

        private static (string Id, string Edge, double Delta)? Syncbase(string item)
        {
            var m = System.Text.RegularExpressions.Regex.Match(item, @"^([A-Za-z_][\w-]*)\.(begin|end)\s*(?:([+-])\s*([\d.]+(?:ms|s|min|h)?))?$");
            if (!m.Success) return null;
            var delta = m.Groups[4].Success ? ArtValues.Seconds(m.Groups[4].Value) ?? 0 : 0;
            if (m.Groups[3].Value == "-") delta = -delta;
            return (m.Groups[1].Value, m.Groups[2].Value, delta);
        }

        private XElement? Target(XElement anim)
        {
            var href = (string?)anim.Attribute("href") ?? (string?)anim.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"));
            if (href is { Length: > 1 } && href.StartsWith('#')) return byId.GetValueOrDefault(href[1..]);
            return anim.Parent;
        }

        private KeyedValues Keyed(XElement el, string? from, Func<string?>? baseValue, bool additiveNumbers = true)
        {
            var kv = new KeyedValues();
            var values = (string?)el.Attribute("values");
            if (!string.IsNullOrWhiteSpace(values))
                kv.Values = values.Split(';').Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
            else
            {
                var start = (string?)el.Attribute("from") ?? from ?? baseValue?.Invoke();
                var to = (string?)el.Attribute("to");
                var by = (string?)el.Attribute("by");
                if (to is null && by is not null && start is not null && additiveNumbers)
                {
                    var a = Affine.Numbers(start);
                    var b = Affine.Numbers(by);
                    to = string.Join(' ', a.Select((v, k) => ArtValues.Format(v + (k < b.Count ? b[k] : 0))));
                }
                if (start is not null) kv.Values.Add(start);
                if (to is not null) kv.Values.Add(to);
            }
            var calc = (string?)el.Attribute("calcMode") ?? (el.Name.LocalName == "animateMotion" ? "paced" : "linear");
            kv.Discrete = calc == "discrete";
            var times = Affine.Numbers((string?)el.Attribute("keyTimes"));
            var n = kv.Values.Count;
            if (times.Count == n && n > 0) kv.Times = times;
            else kv.Times = Enumerable.Range(0, n).Select(k => kv.Discrete ? (double)k / n : n == 1 ? 0 : (double)k / (n - 1)).ToList();
            if (calc == "spline")
            {
                foreach (var spline in ((string?)el.Attribute("keySplines") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries))
                {
                    var p = Affine.Numbers(spline);
                    kv.Easings.Add(p.Count == 4 ? Easing.Bezier(p[0], p[1], p[2], p[3]) : null);
                }
            }
            return kv;
        }

        private Anim? AttributeAnim(XElement el, XElement target, Timing timing)
        {
            var attribute = (string?)el.Attribute("attributeName");
            if (string.IsNullOrWhiteSpace(attribute)) return null;
            var values = Keyed(el, null, () => StaticValue(target, attribute));
            if (values.Values.Count == 0) return null;
            return new SmilAttribute { Target = target, Timing = timing, Attribute = attribute, Values = values };
        }

        private Anim? SetAnim(XElement el, XElement target, Timing timing)
        {
            var attribute = (string?)el.Attribute("attributeName");
            var to = (string?)el.Attribute("to");
            if (string.IsNullOrWhiteSpace(attribute) || to is null) return null;
            if (double.IsInfinity(timing.Dur)) timing.Forwards = true;
            return new SmilAttribute { Target = target, Timing = timing, Attribute = attribute, Values = new KeyedValues { Values = [to], Times = [0] } };
        }

        private Anim? TransformAnim(XElement el, XElement target, Timing timing)
        {
            var type = (string?)el.Attribute("type") ?? "translate";
            if (type is not ("translate" or "scale" or "rotate" or "skewX" or "skewY")) return null;
            var values = Keyed(el, type == "scale" ? "1" : "0", null);
            if (values.Values.Count == 0) return null;
            return new SmilTransform { Target = target, Timing = timing, Type = type, Values = values, Additive = (string?)el.Attribute("additive") == "sum" };
        }

        private Anim? MotionAnim(XElement el, XElement target, Timing timing)
        {
            string? d = (string?)el.Attribute("path");
            var mpath = el.Elements().FirstOrDefault(c => c.Name.LocalName == "mpath");
            if (mpath is not null)
            {
                var href = (string?)mpath.Attribute("href") ?? (string?)mpath.Attribute(XName.Get("href", "http://www.w3.org/1999/xlink"));
                if (href is { Length: > 1 } && byId.TryGetValue(href[1..], out var pathEl)) d = (string?)pathEl.Attribute("d");
            }
            if (string.IsNullOrWhiteSpace(d))
            {
                var points = Keyed(el, "0,0", null).Values.Select(v => Affine.Numbers(v)).Where(n => n.Count >= 2).ToList();
                if (points.Count == 0) return null;
                d = "M" + string.Join(" L", points.Select(n => $"{ArtValues.Format(n[0])},{ArtValues.Format(n[1])}"));
            }
            var geometry = PathGeometry.Parse(d);
            if (geometry.Polylines.Count == 0) return null;
            var keyPoints = Affine.Numbers((string?)el.Attribute("keyPoints"));
            var keyTimes = Affine.Numbers((string?)el.Attribute("keyTimes"));
            return new SmilMotion
            {
                Target = target, Timing = timing, Path = geometry, Rotate = ((string?)el.Attribute("rotate"))?.Trim() ?? "0",
                KeyPoints = keyPoints.Count > 1 ? keyPoints : null, KeyTimes = keyTimes.Count > 1 ? keyTimes : null,
            };
        }

        private void Track(Anim a)
        {
            if (!byTarget.TryGetValue(a.Target, out var list)) byTarget[a.Target] = list = new List<Anim>();
            list.Add(a);
        }

        // ----- What happens when ------------------------------------------------------------------

        /// <summary>The seconds the track covers, and how many loops of a repeating animation that is — or null when nothing moves.</summary>
        public (double Seconds, int Loops)? Timeline()
        {
            byTarget.Clear();
            foreach (var a in anims.OrderBy(a => a.Order)) Track(a);
            var live = anims.Where(a => a.Timing.Dur > 0 || a is SmilAttribute).ToList();
            if (live.Count == 0) return null;
            double finiteEnd = 0, period = 0, lastStart = 0;
            foreach (var a in live)
            {
                var t = a.Timing;
                if (t.Infinite && t.Period > 0)
                {
                    period = Math.Max(period, t.Period);
                    lastStart = Math.Max(lastStart, Math.Max(0, t.Begin));
                }
                // No duration (a set, say): it happens at its begin and stays — show that moment and a little after.
                else if (t.Infinite) { if (t.Begin > 0) finiteEnd = Math.Max(finiteEnd, t.Begin + 0.5); }
                else if (double.IsFinite(t.ActiveDuration)) finiteEnd = Math.Max(finiteEnd, t.Begin + t.ActiveDuration);
            }
            var loops = 1;
            var seconds = finiteEnd;
            if (period > 0)
            {
                loops = LoopsFor(period);
                seconds = Math.Max(finiteEnd, lastStart + period * loops);
            }
            if (seconds <= 0) return null;
            return (Math.Min(seconds, MaxSeconds), loops);
        }

        private IEnumerable<Anim> On(XElement el) => byTarget.TryGetValue(el, out var list) ? list : [];

        private static readonly HashSet<string> MovingProperties = new(StringComparer.Ordinal)
            { "transform", "motion", "opacity", "visibility", "display", "x", "y", "cx", "cy" };

        /// <summary>True when every animation on the element is motion or fading, so it can move as one layer.</summary>
        public bool Moves(XElement el) => On(el).All(a => a is not SmilAttribute { Attribute: "transform" } && a.Properties.All(p =>
            MovingProperties.Contains(p) && (p is not ("x" or "y") || el.Name.LocalName is "rect" or "use" or "image" or "text")
                                         && (p is not ("cx" or "cy") || el.Name.LocalName is "circle" or "ellipse")));

        private static readonly HashSet<string> Fading = new(StringComparer.Ordinal) { "opacity", "visibility", "display" };

        private bool Animated(XElement el) => byTarget.ContainsKey(el);

        /// <summary>A property's animated value at a time (CSS over SMIL, later over earlier), or null when nothing's in effect.</summary>
        private string? ValueAt(XElement el, string property, double t)
        {
            string? value = null;
            foreach (var a in On(el).OrderBy(a => a.Order))
            {
                if (!a.Properties.Contains(property)) continue;
                if (a.Timing.Progress(t) is not { } p) continue;
                value = a switch
                {
                    SmilAttribute s => s.Values.At(p),
                    CssAnim c => c.Value(property, p),
                    _ => value,
                } ?? value;
            }
            return value;
        }

        private double Opacity(XElement el, double t)
        {
            var value = ArtValues.Number(ValueAt(el, "opacity", t) ?? StaticValue(el, "opacity")) ?? 1;
            if ((ValueAt(el, "visibility", t) ?? StaticValue(el, "visibility")) is "hidden" or "collapse") value = 0;
            if ((ValueAt(el, "display", t) ?? StaticValue(el, "display")) == "none") value = 0;
            return Math.Clamp(value, 0, 1);
        }

        /// <summary>The element's own transform at a time. <paramref name="positions"/> folds x/y/cx/cy moves in as a translation.</summary>
        private Affine Local(XElement el, double t, bool positions)
        {
            var m = staticTransform.GetValueOrDefault(el, Affine.Identity);
            Affine? css = null;
            foreach (var a in On(el).OfType<CssAnim>().OrderBy(a => a.Order))
                if (a.Properties.Contains("transform") && a.Timing.Progress(t) is { } p && a.Transform(p) is { } tm) css = tm;
            if (css is { } c) m = c;
            else
                foreach (var a in On(el).OfType<SmilTransform>().OrderBy(a => a.Order))
                    if (a.Timing.Progress(t) is { } p) m = a.Additive ? m * a.At(p) : a.At(p);
            SmilMotion? motion = null;
            double motionP = 0;
            foreach (var a in On(el).OfType<SmilMotion>())
                if (a.Timing.Progress(t) is { } p) { motion = a; motionP = p; }
            if (motion is not null) m = motion.At(motionP) * m;

            if (positions)
            {
                var (xName, yName) = el.Name.LocalName is "circle" or "ellipse" ? ("cx", "cy") : ("x", "y");
                double Shift(string name) =>
                    ValueAt(el, name, t) is { } now && ArtValues.Number(now) is { } n
                        ? n - (ArtValues.Number(StaticValue(el, name)) ?? 0) : 0;
                var (dx, dy) = (Shift(xName), Shift(yName));
                if (dx != 0 || dy != 0) m *= Affine.Translate(dx, dy);
            }
            return m;
        }

        private List<XElement> Chain(XElement el)
        {
            var chain = new List<XElement>();
            for (var a = el; a is not null; a = a.Parent) chain.Add(a);
            chain.Reverse();
            return chain;
        }

        private Affine Ctm(XElement el, double t, bool animated)
        {
            var m = Affine.Identity;
            foreach (var a in Chain(el))
                m *= animated ? Local(a, t, true) : staticTransform.GetValueOrDefault(a, Affine.Identity);
            return m;
        }

        // ----- Cutting into layers ------------------------------------------------------------------

        /// <summary>
        /// The drawn elements in paint order, grouped into runs that move alike (their nearest animated
        /// ancestor-or-self). Null when an animation is somewhere a layer can't carry it.
        /// </summary>
        public List<Segment>? Segments()
        {
            var segments = new List<Segment>();
            var reached = new HashSet<XElement>();
            var ok = true;

            void Walk(XElement el, XElement? owner, bool inText)
            {
                var name = el.Name.LocalName;
                if (Resources.Contains(name) || Ignored.Contains(name) || AnimationTags.Contains(name)) return;
                reached.Add(el);
                if (Animated(el))
                {
                    if (inText) ok = false;
                    owner = el;
                }
                if (Leaves.Contains(name))
                {
                    foreach (var d in el.Descendants()) if (Animated(d)) ok = false; // a tspan moving inside its text
                    if (segments.Count == 0 || segments[^1].Owner != owner) segments.Add(new Segment { Owner = owner });
                    segments[^1].Leaves.Add(el);
                    return;
                }
                foreach (var child in el.Elements()) Walk(child, owner, inText || name == "text");
            }
            Walk(root, null, false);
            foreach (var target in byTarget.Keys) if (!reached.Contains(target)) ok = false;
            return ok ? segments : null;
        }

        /// <summary>A layer's motion: its owner's transform against its resting one, and the fades along its chain, sampled and simplified.</summary>
        public List<ArtFrame> Motion(Segment segment, double seconds)
        {
            var owner = segment.Owner!;
            var chain = Chain(owner).Where(Animated).ToList();
            var faders = chain.Where(a => On(a).Any(x => x.Properties.Any(Fading.Contains))).ToList();
            var rest = Ctm(owner, 0, false).Invert();
            var (cx, cy) = (box.X + box.W / 2, box.Y + box.H / 2);

            var count = 2 * Math.Clamp((int)Math.Ceiling(seconds * 15), 30, 300) + 1;
            var samples = new List<ArtFrame>(count);
            double lastRot = 0;
            for (var k = 0; k < count; k++)
            {
                var u = (double)k / (count - 1);
                var t = u * seconds;
                var m = Ctm(owner, t, true) * rest;
                var (dx, dy, deg, scale) = m.AboutCenter(cx, cy);
                // Unwrap, so a full turn reads 0→360 instead of jumping back at 180.
                if (k > 0) deg = lastRot + (((deg - lastRot) % 360 + 540) % 360 - 180);
                lastRot = deg;
                var opacity = faders.Aggregate(1.0, (o, a) => o * Opacity(a, t));
                samples.Add(new ArtFrame(u, dx, dy, deg, scale, opacity));
            }
            return Simplify(samples, Math.Max(box.W, box.H));
        }

        /// <summary>
        /// A part (or the whole art) as stills at moments of its animation, shown one at a time. A
        /// repeating animation's stills are taken over one loop and shown again for each loop.
        /// </summary>
        public List<ArtLayerPlan> Flipbook(Segment? segment, double seconds, int loops, int frames, int decimals, string name)
        {
            var periodic = loops > 1 && anims.All(a => a.Timing.Infinite && a.Timing.Begin <= 0);
            var span = periodic ? seconds / loops : seconds;
            var steps = FlipbookSteps(frames, periodic ? loops : 1);
            var layers = new List<ArtLayerPlan>();
            for (var j = 0; j < frames; j++)
                layers.Add(new ArtLayerPlan(Layer(segment, decimals, t: span * j / frames), $"{name} {j + 1}", steps[j]));
            return layers;
        }
    }

    /// <summary>How many times a repeating animation of this period plays over a track: about three seconds' worth.</summary>
    public static int LoopsFor(double period) => period <= 0 ? 1 : Math.Clamp((int)Math.Round(3 / period), 1, 4);

    /// <summary>
    /// The opacity keyframes for a flipbook of evenly timed stills played <paramref name="cycles"/> times
    /// over a track: each still on for its slot of every cycle, off otherwise, the last left showing at
    /// the end. Cycles are capped so every still's switches fit the keyframe limit (2 keyframes each).
    /// </summary>
    public static List<List<ArtFrame>> FlipbookSteps(int frames, int cycles)
    {
        cycles = Math.Clamp(cycles, 1, (DesignCatalog.MaxKeyframes - 2) / 4);
        var all = new List<List<ArtFrame>>();
        for (var j = 0; j < frames; j++)
        {
            var windows = new List<(double From, double To)>();
            for (var c = 0; c < cycles; c++)
                windows.Add(((c + (double)j / frames) / cycles, (c + (double)(j + 1) / frames) / cycles));
            all.Add(Steps(windows, j == 0, j == frames - 1));
        }
        return all;
    }

    /// <summary>On during each window and off otherwise, as opacity keyframes with near-instant switches.</summary>
    private static List<ArtFrame> Steps(List<(double From, double To)> windows, bool first, bool last)
    {
        var frames = new List<ArtFrame>();
        bool On(double u) => windows.Any(w => u >= w.From - 1e-9 && u < w.To - 1e-9) || last && u >= windows[^1].To - 1e-9;
        ArtFrame F(double t, bool on) => new(Math.Round(Math.Clamp(t, 0, 1), 5), 0, 0, 0, 1, on ? 1 : 0);
        var state = On(0) || first && windows[0].From <= 0;
        frames.Add(F(0, state));
        var edges = windows.SelectMany(w => new[] { w.From, w.To }).Where(e => e > 0 && e < 1).Distinct().OrderBy(e => e);
        foreach (var edge in edges)
        {
            var next = On(edge);
            if (next == state) continue;
            frames.Add(F(edge - Epsilon, state));
            frames.Add(F(edge, next));
            state = next;
        }
        if (frames[^1].T < 1) frames.Add(F(1, state));
        return frames;
    }

    private sealed partial class Art
    {
        /// <summary>
        /// Ramer–Douglas–Peucker over time, in all five channels at once, loosening until it fits the
        /// keyframe limit. The keyframes play linearly between each other, so this is exactly the error
        /// the viewer would see.
        /// </summary>
        private static List<ArtFrame> Simplify(List<ArtFrame> samples, double size)
        {
            static double[] V(ArtFrame f, double size) => [f.Dx / size, f.Dy / size, f.Rotate / 180, f.Scale - 1, f.Opacity];
            if (samples.All(s => Math.Abs(s.Dx) < size * 1e-4 && Math.Abs(s.Dy) < size * 1e-4 && Math.Abs(s.Rotate) < 0.05
                                  && Math.Abs(s.Scale - 1) < 1e-4 && Math.Abs(s.Opacity - 1) < 1e-4))
                return [];
            var tolerance = 0.002;
            while (true)
            {
                var keep = new bool[samples.Count];
                keep[0] = keep[^1] = true;
                var stack = new Stack<(int, int)>();
                stack.Push((0, samples.Count - 1));
                while (stack.Count > 0)
                {
                    var (a, b) = stack.Pop();
                    if (b - a < 2) continue;
                    var va = V(samples[a], size);
                    var vb = V(samples[b], size);
                    var worst = -1;
                    double worstError = 0;
                    for (var k = a + 1; k < b; k++)
                    {
                        var u = (samples[k].T - samples[a].T) / Math.Max(1e-12, samples[b].T - samples[a].T);
                        var vk = V(samples[k], size);
                        double error = 0;
                        for (var c = 0; c < 5; c++) error = Math.Max(error, Math.Abs(vk[c] - (va[c] + (vb[c] - va[c]) * u)));
                        if (error > worstError) { worstError = error; worst = k; }
                    }
                    if (worstError > tolerance && worst > 0)
                    {
                        keep[worst] = true;
                        stack.Push((a, worst));
                        stack.Push((worst, b));
                    }
                }
                var kept = samples.Where((_, k) => keep[k]).ToList();
                if (kept.Count <= DesignCatalog.MaxKeyframes)
                    return kept.Select(f => f with
                    {
                        T = Math.Round(f.T, 5), Dx = Math.Round(f.Dx, 3), Dy = Math.Round(f.Dy, 3), Rotate = Math.Round(f.Rotate, 2),
                        Scale = Math.Round(f.Scale, 4), Opacity = Math.Round(f.Opacity, 3),
                    }).ToList();
                tolerance *= 1.4;
            }
        }

        // ----- Writing a layer ----------------------------------------------------------------------

        /// <summary>
        /// The art with only a segment's drawn elements (null for all of them), no animation, and only
        /// the definitions those elements use. With a time, every animated value is written in as it is
        /// at that moment (a flipbook still); without one, a moving layer's own motion and fades are
        /// left out, since its keyframes carry them.
        /// </summary>
        public string Layer(Segment? segment, int decimals, double? t = null)
        {
            var clone = new XElement(root);
            var keep = new HashSet<int>();
            if (segment is not null)
                foreach (var leaf in segment.Leaves)
                    for (var a = leaf; a is not null; a = a.Parent) keep.Add(keys[a]);

            // Children are rebuilt in one go rather than removed one by one: XNode.Remove walks the
            // sibling list, so removing thousands of siblings singly is quadratic (10 s on one frame).
            void Prune(XElement el)
            {
                var kept = new List<XNode>();
                var changed = false;
                foreach (var node in el.Nodes())
                {
                    if (node is not XElement child) { kept.Add(node); continue; }
                    var name = child.Name.LocalName;
                    var drop = AnimationTags.Contains(name) || Ignored.Contains(name)
                               || !Resources.Contains(name) && segment is not null && !keep.Contains((int)child.Attribute(Marker)!);
                    if (drop) { changed = true; continue; }
                    kept.Add(child);
                    if (!Resources.Contains(name) && !Leaves.Contains(name)) Prune(child);
                }
                if (changed) el.ReplaceNodes(kept);
            }
            Prune(clone);
            foreach (var junk in clone.Descendants().Where(e => AnimationTags.Contains(e.Name.LocalName) || Ignored.Contains(e.Name.LocalName)).ToList())
                junk.Remove();

            foreach (var el in clone.DescendantsAndSelf())
            {
                var original = byKey[(int)el.Attribute(Marker)!];
                if (!Animated(original)) continue;
                if (t is { } time) Bake(original, el, time);
                else if (segment?.Owner is { } owner && Chain(owner).Contains(original) && On(original).Any(a => a.Properties.Any(Fading.Contains)))
                {
                    el.SetAttributeValue("opacity", null);
                    el.SetAttributeValue("visibility", null);
                    el.SetAttributeValue("display", null);
                    SetStyle(el, "opacity", null);
                    SetStyle(el, "visibility", null);
                    SetStyle(el, "display", null);
                }
            }

            // What never shows (Inkscape's hidden layers) and nothing animates into view is dead weight —
            // unless it's drawn elsewhere through <use>.
            var used = new HashSet<string>(clone.DescendantsAndSelf().SelectMany(References), StringComparer.Ordinal);
            foreach (var el in clone.Descendants().Where(e => !Resources.Contains(e.Name.LocalName) && !InResource(e)).ToList())
                if (el.Parent is not null && t is null && Hidden(el) && !Animated(byKey[(int)el.Attribute(Marker)!])
                    && !el.DescendantsAndSelf().Any(d => (string?)d.Attribute("id") is { } id && used.Contains(id)))
                    el.Remove();

            if (t is null) MergeRuns(clone);
            PruneUnused(clone);
            var referenced = new HashSet<string>(clone.DescendantsAndSelf().SelectMany(References), StringComparer.Ordinal);
            foreach (var el in clone.DescendantsAndSelf())
            {
                el.SetAttributeValue(Marker, null);
                el.SetAttributeValue("class", null);
                if ((string?)el.Attribute("id") is { } id && !referenced.Contains(id)) el.SetAttributeValue("id", null);
                if (decimals >= 0)
                    foreach (var attr in el.Attributes().Where(a => CompactAttributes.Contains(a.Name.LocalName)).ToList())
                        attr.Value = attr.Name.LocalName == "d"
                            ? PathData.Compact(attr.Value, decimals)
                            : ArtValues.Compact(attr.Value, attr.Name.LocalName.EndsWith("ransform", StringComparison.Ordinal) ? Math.Max(decimals, 4) : decimals);
            }
            // Groups that carry nothing are just nesting: lift their children out; empty ones go.
            foreach (var g in clone.Descendants().Where(e => e.Name.LocalName == "g").Reverse().ToList())
            {
                if (!g.HasElements) g.Remove();
                else if (!g.HasAttributes) g.ReplaceWith(g.Elements());
            }
            clone.SetAttributeValue("viewBox", string.Join(' ', new[] { box.X, box.Y, box.W, box.H }.Select(ArtValues.Format)));
            clone.SetAttributeValue("width", null);
            clone.SetAttributeValue("height", null);
            clone.SetAttributeValue("style", null);
            clone.SetAttributeValue("transform", null);
            return clone.ToString(SaveOptions.DisableFormatting);
        }

        /// <summary>
        /// Merges runs of neighbouring rectangles and polygons that look the same (every attribute but
        /// their geometry equal) into one path each. Traced and mosaic art is tens of thousands of these —
        /// one real frame was 16,000 shapes in 43 runs. Only neighbours merge, so paint order holds; each
        /// piece is one outline, turned to the same winding, so overlaps can't cut holes under nonzero
        /// fill; anything whose look depends on being separate (opacity, an id something points at) stays.
        /// Zero-sized shapes, which draw nothing, go.
        /// </summary>
        private static void MergeRuns(XElement root)
        {
            foreach (var parent in root.DescendantsAndSelf().Where(e => e.HasElements && !e.Name.LocalName.StartsWith("text", StringComparison.Ordinal)).ToList())
            {
                var output = new List<XNode>();
                var changed = false;
                var run = new List<(XElement El, string D)>();
                string? signature = null;
                void Flush()
                {
                    if (run.Count > 1)
                    {
                        var first = run[0].El;
                        output.Add(new XElement(first.Name.Namespace + "path",
                            first.Attributes().Where(a => !Geometry(a.Name.LocalName) && a.Name.LocalName != "fill-rule"),
                            new XAttribute("d", string.Concat(run.Select(r => r.D)))));
                        changed = true;
                    }
                    else output.AddRange(run.Select(r => r.El));
                    run.Clear();
                    signature = null;
                }
                foreach (var node in parent.Nodes())
                {
                    // Whitespace between shapes means nothing outside text; it mustn't end a run.
                    if (node is XText { Value: var text } && string.IsNullOrWhiteSpace(text)) { changed = true; continue; }
                    if (node is not XElement child) { Flush(); output.Add(node); continue; }
                    if (Invisible(child)) { changed = true; continue; }
                    var d = Outline(child);
                    var sig = d is null ? null : Signature(child);
                    if (sig is null) { Flush(); output.Add(child); continue; }
                    if (sig != signature) Flush();
                    signature = sig;
                    run.Add((child, d!));
                }
                Flush();
                if (changed) parent.ReplaceNodes(output);
            }
        }

        private static bool Geometry(string name) => name is "x" or "y" or "width" or "height" or "points" or "rx" or "ry" or Marker;

        /// <summary>Everything about how a shape looks except where it is, or null when it mustn't merge.</summary>
        private static string? Signature(XElement el)
        {
            var attrs = el.Attributes().Where(a => !Geometry(a.Name.LocalName)).ToList();
            foreach (var a in attrs)
            {
                var name = a.Name.LocalName;
                if (name is "id" or "opacity" or "fill-opacity" or "stroke-opacity" or "filter" or "mask" or "clip-path") return null;
                if (name == "style" && (a.Value.Contains("opacity", StringComparison.Ordinal) || a.Value.Contains("filter", StringComparison.Ordinal))) return null;
                if (name == "fill" && a.Value.Contains("url(", StringComparison.Ordinal)) return null; // a gradient spans each shape's own box
            }
            return string.Join('\u0001', attrs.OrderBy(a => a.Name.LocalName, StringComparer.Ordinal).Select(a => a.Name.LocalName + "=" + a.Value));
        }

        /// <summary>A rectangle or polygon as one outline wound the same way (positive area, y down), or null.</summary>
        private static string? Outline(XElement el)
        {
            var inv = CultureInfo.InvariantCulture;
            switch (el.Name.LocalName)
            {
                case "rect":
                {
                    if (el.Attribute("rx") is not null || el.Attribute("ry") is not null) return null;
                    var w = ArtValues.Number((string?)el.Attribute("width"));
                    var h = ArtValues.Number((string?)el.Attribute("height"));
                    if (w is not > 0 || h is not > 0 || ((string?)el.Attribute("width"))!.Contains('%') || ((string?)el.Attribute("height"))!.Contains('%')) return null;
                    var x = ArtValues.Number((string?)el.Attribute("x")) ?? 0;
                    var y = ArtValues.Number((string?)el.Attribute("y")) ?? 0;
                    return string.Create(inv, $"M{x},{y}h{w}v{h}h{-w}z");
                }
                case "polygon":
                {
                    var n = Affine.Numbers((string?)el.Attribute("points"));
                    if (n.Count < 6) return null;
                    var pts = Enumerable.Range(0, n.Count / 2).Select(k => (X: n[2 * k], Y: n[2 * k + 1])).ToList();
                    double area = 0;
                    for (var k = 0; k < pts.Count; k++)
                    {
                        var (ax, ay) = pts[k];
                        var (bx, by) = pts[(k + 1) % pts.Count];
                        area += ax * by - bx * ay;
                    }
                    if (area < 0) pts.Reverse();
                    return "M" + string.Join('L', pts.Select(p => string.Create(inv, $"{p.X},{p.Y}"))) + "z";
                }
                default:
                    return null;
            }
        }

        /// <summary>A shape with no size draws nothing (traced art is full of r="0" circles).</summary>
        private static bool Invisible(XElement el)
        {
            if (el.HasElements || el.Attribute("id") is not null) return false;
            double? A(string n) => ArtValues.Number((string?)el.Attribute(n));
            return el.Name.LocalName switch
            {
                "circle" => A("r") is not > 0,
                "ellipse" => A("rx") is not > 0 || A("ry") is not > 0,
                "rect" => el.Attribute("width") is not null && A("width") is not > 0 || el.Attribute("height") is not null && A("height") is not > 0,
                _ => false,
            };
        }

        private static bool Hidden(XElement el)
        {
            if ((string?)el.Attribute("display") == "none" || (string?)el.Attribute("visibility") == "hidden") return true;
            var style = (string?)el.Attribute("style");
            return style is not null && MiniCss.Declarations(style).Any(d =>
                d.Key == "display" && d.Value == "none" || d.Key == "visibility" && d.Value == "hidden");
        }

        /// <summary>Writes an element's animated values at a moment into its attributes and style.</summary>
        private void Bake(XElement original, XElement el, double t)
        {
            var local = Local(original, t, positions: false);
            el.SetAttributeValue("transform", local.IsIdentity() ? null : local.ToSvg());
            var properties = On(original).SelectMany(a => a.Properties).Where(p => p is not ("transform" or "motion")).Distinct();
            foreach (var property in properties)
            {
                var value = ValueAt(original, property, t);
                if (value is null) continue;
                if (GeometryAttributes.Contains(property))
                {
                    el.SetAttributeValue(property, value);
                }
                else
                {
                    el.SetAttributeValue(property, null);
                    SetStyle(el, property, value);
                }
            }
        }

        private static void SetStyle(XElement el, string property, string? value)
        {
            var decls = MiniCss.Declarations((string?)el.Attribute("style")).Where(d => d.Key != property).ToList();
            if (value is not null) decls.Add(new(property, value));
            var joined = string.Join(';', decls.Select(d => $"{d.Key}:{d.Value}"));
            el.SetAttributeValue("style", joined.Length > 0 ? joined : null);
        }

        /// <summary>Drops definitions nothing drawn refers to (directly or through other definitions).</summary>
        private static void PruneUnused(XElement clone)
        {
            var defsById = new Dictionary<string, XElement>(StringComparer.Ordinal);
            foreach (var el in clone.Descendants())
                if (Resources.Contains(el.Name.LocalName) && el.Name.LocalName != "defs" && (string?)el.Attribute("id") is { } id)
                    defsById.TryAdd(id, el);
            var used = new HashSet<string>(StringComparer.Ordinal);
            var queue = new Queue<XElement>(clone.Descendants().Where(e => !InResource(e)));
            while (queue.Count > 0)
            {
                var el = queue.Dequeue();
                foreach (var id in References(el))
                    if (used.Add(id) && defsById.TryGetValue(id, out var def))
                        foreach (var d in def.DescendantsAndSelf()) queue.Enqueue(d);
            }
            foreach (var (id, el) in defsById)
                if (!used.Contains(id) && el.Parent is not null) el.Remove();
            foreach (var defs in clone.Descendants().Where(e => e.Name.LocalName == "defs").ToList())
                if (!defs.HasElements) defs.Remove();
        }

        private static bool InResource(XElement el)
        {
            for (var a = el; a is not null; a = a.Parent)
                if (Resources.Contains(a.Name.LocalName)) return true;
            return false;
        }

        private static IEnumerable<string> References(XElement el)
        {
            foreach (var attr in el.Attributes())
            {
                var v = attr.Value;
                if (attr.Name.LocalName == "href" && v.StartsWith('#')) yield return v[1..];
                var i = v.IndexOf("url(", StringComparison.Ordinal);
                while (i >= 0)
                {
                    var hash = v.IndexOf('#', i);
                    var close = v.IndexOf(')', i);
                    if (hash > i && close > hash) yield return v[(hash + 1)..close].Trim().Trim('\'', '"');
                    i = close < 0 ? -1 : v.IndexOf("url(", close, StringComparison.Ordinal);
                }
            }
        }

        // ----- Geometry -----------------------------------------------------------------------------

        /// <summary>An element's box in its own coordinates (before its transform), or null when it can't be measured.</summary>
        public Box? Bounds(XElement el)
        {
            double A(string name) => ArtValues.Number((string?)el.Attribute(name)) ?? 0;
            switch (el.Name.LocalName)
            {
                case "rect":
                case "use":
                case "image":
                    return A("width") > 0 && A("height") > 0 ? new Box(A("x"), A("y"), A("width"), A("height")) : null;
                case "circle":
                    return new Box(A("cx") - A("r"), A("cy") - A("r"), 2 * A("r"), 2 * A("r"));
                case "ellipse":
                    return new Box(A("cx") - A("rx"), A("cy") - A("ry"), 2 * A("rx"), 2 * A("ry"));
                case "line":
                    return FromPoints([(A("x1"), A("y1")), (A("x2"), A("y2"))]);
                case "polyline":
                case "polygon":
                {
                    var n = Affine.Numbers((string?)el.Attribute("points"));
                    return FromPoints(Enumerable.Range(0, n.Count / 2).Select(k => (n[2 * k], n[2 * k + 1])));
                }
                case "path":
                {
                    var b = PathGeometry.Parse((string?)el.Attribute("d")).Bounds();
                    return b is { } r ? new Box(r.MinX, r.MinY, r.MaxX - r.MinX, r.MaxY - r.MinY) : null;
                }
                case "g":
                case "svg":
                {
                    var points = new List<(double, double)>();
                    foreach (var child in el.Elements())
                    {
                        if (Resources.Contains(child.Name.LocalName) || Ignored.Contains(child.Name.LocalName)) continue;
                        if (Bounds(child) is not { } cb) continue;
                        var m = staticTransform.GetValueOrDefault(child, Affine.Parse((string?)child.Attribute("transform")));
                        points.Add(m.Apply(cb.X, cb.Y));
                        points.Add(m.Apply(cb.X + cb.W, cb.Y));
                        points.Add(m.Apply(cb.X, cb.Y + cb.H));
                        points.Add(m.Apply(cb.X + cb.W, cb.Y + cb.H));
                    }
                    return FromPoints(points);
                }
                default:
                    return null;
            }
        }

        private static Box? FromPoints(IEnumerable<(double X, double Y)> points)
        {
            var list = points.ToList();
            if (list.Count == 0) return null;
            var (minX, minY) = (list.Min(p => p.X), list.Min(p => p.Y));
            return new Box(minX, minY, list.Max(p => p.X) - minX, list.Max(p => p.Y) - minY);
        }
    }

    // ================================================================================================
    // CSS transforms
    // ================================================================================================

    internal static class CssTransform
    {
        private static readonly System.Text.RegularExpressions.Regex FunctionRegex =
            new(@"([a-zA-Z0-9]+)\s*\(([^)]*)\)", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// A CSS transform list as functions with plain-number arguments: degrees for angles, user units
        /// for lengths. Percentages resolve against <paramref name="refW"/>×<paramref name="refH"/>.
        /// </summary>
        public static List<(string Name, double[] Args)> Parse(string? value, double refW, double refH)
        {
            var list = new List<(string, double[])>();
            if (string.IsNullOrWhiteSpace(value) || value.Trim() == "none") return list;
            foreach (System.Text.RegularExpressions.Match f in FunctionRegex.Matches(value))
            {
                var name = f.Groups[1].Value;
                var args = MiniCss.SplitTopLevel(f.Groups[2].Value.Replace(' ', ','), ',')
                    .Select((a, k) => Argument(a, name, k, refW, refH)).ToArray();
                list.Add((name, args));
            }
            return list;
        }

        private static double Argument(string raw, string fn, int index, double refW, double refH)
        {
            var a = raw.Trim().ToLowerInvariant();
            var n = ArtValues.Number(a) ?? 0;
            if (a.EndsWith("deg")) return n;
            if (a.EndsWith("grad")) return n * 0.9;
            if (a.EndsWith("rad")) return n * 180 / Math.PI;
            if (a.EndsWith("turn")) return n * 360;
            // Number() already divided a percentage by 100.
            if (a.EndsWith('%')) return n * (fn == "translateY" || index == 1 ? refH : refW);
            return n;
        }

        /// <summary>
        /// A transform list rewritten one way — <c>name(a,b)</c>, units kept, the short forms filled out
        /// (<c>translate(x)</c> is <c>translate(x,0)</c>, <c>scale(s)</c> is <c>scale(s,s)</c>) — so two stops
        /// that mean the same shape of transform read the same apart from their numbers.
        /// </summary>
        public static List<string> Canonical(string? value)
        {
            var list = new List<string>();
            if (string.IsNullOrWhiteSpace(value) || value.Trim() == "none") return list;
            foreach (System.Text.RegularExpressions.Match f in FunctionRegex.Matches(value))
            {
                var name = f.Groups[1].Value;
                var args = MiniCss.SplitTopLevel(f.Groups[2].Value.Replace(' ', ','), ',').Select(a => a.Trim().ToLowerInvariant()).ToList();
                if (name.StartsWith("rotate") || name.StartsWith("skew")) args = args.Select(a => a == "0" ? "0deg" : a).ToList();
                if (args.Count == 1 && name == "translate") args.Add("0px");
                if (args.Count == 1 && name == "scale") args.Add(args[0]);
                args = args.Select(a => a.EndsWith("px") ? a[..^2] : a).Select(Angle).ToList();
                list.Add($"{name}({string.Join(',', args)})");
            }
            return list;
        }

        /// <summary>An angle in degrees whatever unit it came in; anything else as it was.</summary>
        private static string Angle(string a)
        {
            if (a.EndsWith("deg") || !(a.EndsWith("turn") || a.EndsWith("rad") || a.EndsWith("grad"))) return a;
            var n = ArtValues.Number(a) ?? 0;
            var deg = a.EndsWith("turn") ? n * 360 : a.EndsWith("grad") ? n * 0.9 : n * 180 / Math.PI;
            return ArtValues.Format(deg) + "deg";
        }

        /// <summary>The same function doing nothing: <c>rotate(0deg)</c>, <c>scale(1,1)</c>.</summary>
        public static string Neutral(string canonical)
        {
            var open = canonical.IndexOf('(');
            var name = canonical[..open];
            var neutral = name.StartsWith("scale", StringComparison.Ordinal) ? "1" : "0";
            return System.Text.RegularExpressions.Regex.Replace(canonical,
                @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?", neutral);
        }

        public static Affine Function(string name, double[] a)
        {
            double At(int i, double d = 0) => i < a.Length ? a[i] : d;
            return name switch
            {
                "translate" => Affine.Translate(At(0), At(1)),
                "translateX" => Affine.Translate(At(0), 0),
                "translateY" => Affine.Translate(0, At(0)),
                "translate3d" => Affine.Translate(At(0), At(1)),
                "scale" => Affine.Scale(At(0, 1), a.Length > 1 ? a[1] : At(0, 1)),
                "scaleX" => Affine.Scale(At(0, 1), 1),
                "scaleY" => Affine.Scale(1, At(0, 1)),
                "scale3d" => Affine.Scale(At(0, 1), At(1, 1)),
                "rotate" or "rotateZ" => Affine.Rotate(At(0)),
                "skew" => Affine.SkewX(At(0)) * Affine.SkewY(At(1)),
                "skewX" => Affine.SkewX(At(0)),
                "skewY" => Affine.SkewY(At(0)),
                "matrix" when a.Length >= 6 => new Affine(a[0], a[1], a[2], a[3], a[4], a[5]),
                _ => Affine.Identity,
            };
        }

        /// <summary>A <c>transform-origin</c> as a point in the element's user space.</summary>
        public static (double X, double Y) Origin(string? value, double x, double y, double w, double h, bool fillBox)
        {
            if (string.IsNullOrWhiteSpace(value)) return fillBox ? (x, y) : (0, 0);
            var parts = value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            double? ox = null, oy = null;
            foreach (var p in parts.Take(2))
            {
                switch (p)
                {
                    case "left": ox = 0; break;
                    case "right": ox = w; break;
                    case "top": oy = 0; break;
                    case "bottom": oy = h; break;
                    case "center":
                        if (ox is null) ox = w / 2; else oy = h / 2;
                        break;
                    default:
                        var n = ArtValues.Number(p) ?? 0;
                        var isPct = p.EndsWith('%');
                        if (ox is null) ox = isPct ? n * w : n;
                        else oy = isPct ? n * h : n;
                        break;
                }
            }
            return (x + (ox ?? w / 2), y + (oy ?? h / 2));
        }
    }
}
