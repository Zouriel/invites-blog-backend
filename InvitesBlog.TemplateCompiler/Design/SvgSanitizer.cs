using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace InvitesBlog.TemplateCompiler.Design;

public sealed record SanitizedSvg(
    string ViewBox,
    string InnerMarkup,
    IReadOnlyList<string> Colors,
    double Width,
    double Height)
{
    /// <summary>The whole document, as stored on a <see cref="DesignAsset"/>.</summary>
    public string Document => $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{ViewBox}\">{InnerMarkup}</svg>";
}

public sealed class SvgRejectedException(string message) : Exception(message);

/// <summary>
/// Reduces an SVG to drawing and nothing else. It is an ALLOWLIST: an element or attribute not named
/// here is dropped, so a feature SVG grows next year is excluded until someone decides otherwise.
/// Gone: scripts, event handlers, <c>foreignObject</c>, embedded images, stylesheets, animation
/// elements, and every reference that leaves the document (<c>href</c> and <c>url()</c> may only
/// point at <c>#id</c>).
///
/// <para>Each distinct paint colour is rewritten to <c>var(--cN, #original)</c> in a <c>style</c>, which
/// is how the designer recolours an imported SVG with theme variables without touching its markup
/// again. Ids are prefixed so two SVGs on one page can't capture each other's gradients.</para>
///
/// <para>Runs at import AND at every compile, because a scene is client-supplied JSON: what was
/// sanitised when it was uploaded is not what arrives next time.</para>
/// </summary>
public static partial class SvgSanitizer
{
    private const string SvgNs = "http://www.w3.org/2000/svg";
    private const string XlinkNs = "http://www.w3.org/1999/xlink";
    private const int MaxNodes = 8000;
    private const int MaxDepth = 40;

    private static readonly HashSet<string> Elements = new(StringComparer.Ordinal)
    {
        "g", "path", "rect", "circle", "ellipse", "line", "polyline", "polygon", "defs", "linearGradient",
        "radialGradient", "stop", "clipPath", "mask", "use", "symbol", "text", "tspan", "pattern",
        "filter", "feGaussianBlur", "feOffset", "feBlend", "feColorMatrix", "feMerge", "feMergeNode",
        "feFlood", "feComposite", "feDropShadow",
    };

    private static readonly HashSet<string> Attributes = new(StringComparer.Ordinal)
    {
        "id", "d", "x", "y", "x1", "y1", "x2", "y2", "cx", "cy", "r", "rx", "ry", "fx", "fy", "width", "height",
        "points", "transform", "fill", "stroke", "stroke-width", "stroke-linecap", "stroke-linejoin",
        "stroke-miterlimit", "stroke-dasharray", "stroke-dashoffset", "stroke-opacity", "fill-opacity",
        "fill-rule", "clip-rule", "opacity", "offset", "stop-color", "stop-opacity", "gradientUnits",
        "gradientTransform", "spreadMethod", "clip-path", "mask", "maskUnits", "maskContentUnits",
        "clipPathUnits", "patternUnits", "patternContentUnits", "patternTransform", "viewBox",
        "preserveAspectRatio", "font-family", "font-size", "font-weight", "font-style", "text-anchor",
        "letter-spacing", "dominant-baseline", "filter", "stdDeviation", "in", "in2", "result", "dx", "dy",
        "mode", "values", "type", "operator", "k1", "k2", "k3", "k4", "flood-color", "flood-opacity", "style",
        "href", "visibility", "display", "vector-effect", "paint-order", "filterUnits", "primitiveUnits",
    };

    private static readonly HashSet<string> PaintProperties = new(StringComparer.Ordinal)
    {
        "fill", "stroke", "stop-color", "flood-color",
    };

    private static readonly Dictionary<string, string> NamedColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["black"] = "#000000", ["white"] = "#ffffff", ["red"] = "#ff0000", ["green"] = "#008000",
        ["blue"] = "#0000ff", ["yellow"] = "#ffff00", ["gold"] = "#ffd700", ["silver"] = "#c0c0c0",
        ["gray"] = "#808080", ["grey"] = "#808080", ["orange"] = "#ffa500", ["purple"] = "#800080",
        ["pink"] = "#ffc0cb", ["navy"] = "#000080", ["teal"] = "#008080", ["maroon"] = "#800000",
        ["olive"] = "#808000", ["brown"] = "#a52a2a", ["beige"] = "#f5f5dc", ["ivory"] = "#fffff0",
    };

    [GeneratedRegex(@"url\(\s*['""]?\s*#([A-Za-z0-9_.:-]+)\s*['""]?\s*\)")]
    private static partial Regex LocalUrlRegex();

    [GeneratedRegex(@"^var\(--c(\d{1,3}),\s*(#[0-9a-f]{6})\)$")]
    private static partial Regex PaintVarRegex();

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:,\s*[\d.]+\s*)?\)$", RegexOptions.IgnoreCase)]
    private static partial Regex RgbRegex();

    [GeneratedRegex(@"^[A-Za-z0-9_.:-]{1,80}$")]
    private static partial Regex IdRegex();

    public static SanitizedSvg Sanitize(string markup, string idPrefix = "")
    {
        if (string.IsNullOrWhiteSpace(markup)) throw new SvgRejectedException("The SVG is empty.");

        XDocument doc;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 4_000_000,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
            };
            using var reader = XmlReader.Create(new StringReader(markup), settings);
            doc = XDocument.Load(reader);
        }
        catch (XmlException e)
        {
            throw new SvgRejectedException($"That file isn't a valid SVG ({e.Message}).");
        }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "svg" || (root.Name.NamespaceName is not ("" or SvgNs)))
            throw new SvgRejectedException("That file isn't an SVG.");

        var state = new State(idPrefix);
        var inner = new StringBuilder();
        foreach (var child in root.Nodes()) Write(child, inner, state, depth: 1);

        var (viewBox, width, height) = ViewBoxOf(root);
        return new SanitizedSvg(viewBox, inner.ToString(), state.Colors, width, height);
    }

    private sealed class State(string prefix)
    {
        public string Prefix { get; } = prefix;
        public List<string> Colors { get; } = new();
        public int Nodes;
    }

    private static void Write(XNode node, StringBuilder sb, State state, int depth)
    {
        if (++state.Nodes > MaxNodes) throw new SvgRejectedException("That SVG is too complex.");
        if (depth > MaxDepth) throw new SvgRejectedException("That SVG is nested too deeply.");

        if (node is XText text)
        {
            // Text only means anything inside <text>/<tspan>; elsewhere it's whitespace or junk.
            if (text.Parent?.Name.LocalName is "text" or "tspan")
                sb.Append(WebUtility.HtmlEncode(text.Value));
            return;
        }
        if (node is not XElement el) return;
        if (el.Name.NamespaceName is not ("" or SvgNs)) return;
        var name = el.Name.LocalName;
        if (!Elements.Contains(name)) return;

        sb.Append('<').Append(name);
        var styleParts = new List<string>();
        foreach (var attr in el.Attributes())
        {
            if (attr.IsNamespaceDeclaration) continue;
            var attrName = attr.Name.NamespaceName == XlinkNs && attr.Name.LocalName == "href"
                ? "href"
                : attr.Name.NamespaceName == "" ? attr.Name.LocalName : null;
            if (attrName is null || !Attributes.Contains(attrName)) continue;
            var value = attr.Value.Trim();

            if (attrName == "style")
            {
                foreach (var declaration in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var colon = declaration.IndexOf(':');
                    if (colon <= 0) continue;
                    var prop = declaration[..colon].Trim().ToLowerInvariant();
                    var propValue = declaration[(colon + 1)..].Trim();
                    if (prop == "style" || prop == "href" || prop == "id" || !Attributes.Contains(prop)) continue;
                    var cleaned = CleanValue(prop, propValue, state);
                    if (cleaned is null) continue;
                    if (PaintProperties.Contains(prop)) styleParts.Add($"{prop}:{cleaned}");
                    else styleParts.Add($"{prop}:{cleaned}");
                }
                continue;
            }

            if (attrName == "href")
            {
                if (!value.StartsWith('#') || !IdRegex().IsMatch(value[1..])) continue;
                sb.Append(" href=\"#").Append(WebUtility.HtmlEncode(state.Prefix + value[1..])).Append('"');
                continue;
            }

            if (attrName == "id")
            {
                if (!IdRegex().IsMatch(value)) continue;
                sb.Append(" id=\"").Append(WebUtility.HtmlEncode(state.Prefix + value)).Append('"');
                continue;
            }

            var clean = CleanValue(attrName, value, state);
            if (clean is null) continue;
            // Paint moves into style so it can hold a var() — presentation attributes can't.
            if (PaintProperties.Contains(attrName) && clean.StartsWith("var(", StringComparison.Ordinal))
            {
                styleParts.Add($"{attrName}:{clean}");
                continue;
            }
            sb.Append(' ').Append(attrName).Append("=\"").Append(WebUtility.HtmlEncode(clean)).Append('"');
        }
        if (styleParts.Count > 0)
            sb.Append(" style=\"").Append(WebUtility.HtmlEncode(string.Join(';', styleParts))).Append('"');

        var children = el.Nodes().ToList();
        if (children.Count == 0)
        {
            sb.Append("/>");
            return;
        }
        sb.Append('>');
        foreach (var child in children) Write(child, sb, state, depth + 1);
        sb.Append("</").Append(name).Append('>');
    }

    /// <summary>A safe value for the property, or null to drop it.</summary>
    private static string? CleanValue(string property, string value, State state)
    {
        if (value.Length == 0 || value.Length > 20000) return null;
        foreach (var ch in value)
            if (ch is '<' or '>' or '\\' or '"' or '`' || char.IsControl(ch) && ch is not ('\n' or '\r' or '\t'))
                return null;
        var lower = value.ToLowerInvariant();
        if (lower.Contains("javascript") || lower.Contains("expression") || lower.Contains("@import")
            || lower.Contains("data:") || lower.Contains("http") || lower.Contains("//"))
            return null;

        // url() only to a local id — and rewritten to the prefixed id.
        if (lower.Contains("url("))
        {
            var rewritten = LocalUrlRegex().Replace(value, m => $"url(#{state.Prefix}{m.Groups[1].Value})");
            var withoutLocal = LocalUrlRegex().Replace(rewritten, string.Empty);
            if (withoutLocal.Contains("url(", StringComparison.OrdinalIgnoreCase)) return null;
            return rewritten;
        }

        if (PaintProperties.Contains(property)) return Paint(value, state);
        if (lower.Contains("var(")) return null;
        return value;
    }

    private static string? Paint(string value, State state)
    {
        var v = value.Trim();
        var lower = v.ToLowerInvariant();
        if (lower is "none" or "transparent" or "currentcolor" or "inherit") return lower == "currentcolor" ? "currentColor" : lower;

        // Already recoloured on a previous pass: keep the slot, re-validate the fallback.
        var existing = PaintVarRegex().Match(lower);
        if (existing.Success)
        {
            var hex = existing.Groups[2].Value;
            var index = RegisterColor(hex, state);
            return $"var(--c{index}, {hex})";
        }

        var normalized = NormalizeColor(v);
        if (normalized is null) return null;
        var slot = RegisterColor(normalized, state);
        return $"var(--c{slot}, {normalized})";
    }

    private static int RegisterColor(string hex, State state)
    {
        var index = state.Colors.IndexOf(hex);
        if (index >= 0) return index;
        state.Colors.Add(hex);
        return state.Colors.Count - 1;
    }

    /// <summary>A colour as lowercase <c>#rrggbb</c>, or null when it isn't one we recognise.</summary>
    public static string? NormalizeColor(string value)
    {
        var v = value.Trim();
        if (NamedColors.TryGetValue(v, out var named)) return named;
        if (v.StartsWith('#'))
        {
            var hex = v[1..];
            if (!hex.All(char.IsAsciiHexDigit)) return null;
            return hex.Length switch
            {
                3 or 4 => "#" + string.Concat(hex[..3].Select(c => $"{c}{c}")).ToLowerInvariant(),
                6 or 8 => "#" + hex[..6].ToLowerInvariant(),
                _ => null,
            };
        }
        var rgb = RgbRegex().Match(v);
        if (rgb.Success)
        {
            var parts = Enumerable.Range(1, 3).Select(i => Math.Clamp(int.Parse(rgb.Groups[i].Value, CultureInfo.InvariantCulture), 0, 255));
            return "#" + string.Concat(parts.Select(p => p.ToString("x2", CultureInfo.InvariantCulture)));
        }
        return null;
    }

    private static (string ViewBox, double Width, double Height) ViewBoxOf(XElement root)
    {
        var vb = root.Attribute("viewBox")?.Value;
        if (vb is not null)
        {
            var nums = vb.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(p => double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN)
                .ToArray();
            if (nums.Length == 4 && nums.All(double.IsFinite) && nums[2] > 0 && nums[3] > 0)
                return (string.Join(' ', nums.Select(DesignCss.Num)), nums[2], nums[3]);
        }
        var w = Length(root.Attribute("width")?.Value) ?? 100;
        var h = Length(root.Attribute("height")?.Value) ?? 100;
        return ($"0 0 {DesignCss.Num(w)} {DesignCss.Num(h)}", w, h);
    }

    private static double? Length(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var digits = new string(value.Trim().TakeWhile(c => char.IsAsciiDigit(c) || c == '.').ToArray());
        return double.TryParse(digits, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d : null;
    }
}
