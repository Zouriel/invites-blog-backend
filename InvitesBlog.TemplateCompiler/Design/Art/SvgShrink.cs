using System.Xml.Linq;

namespace InvitesBlog.TemplateCompiler.Design.Art;

/// <summary>
/// Makes an illustration smaller for the size it's shown at. A vector has no pixels to drop, so what goes
/// is precision nobody can see: coordinates are rounded to the decimals that keep every point within a
/// tenth of a screen pixel at that size (<see cref="DecimalsFor"/>), and path data is rewritten short
/// (<see cref="PathData.Compact"/>). Shown bigger later, it's still sharp unless it's shown many times
/// bigger, which is when the extra decimals would start to matter.
/// </summary>
public static class SvgShrink
{
    private static readonly HashSet<string> Numeric = new(StringComparer.Ordinal)
    {
        "d", "points", "transform", "x", "y", "cx", "cy", "r", "rx", "ry", "width", "height", "x1", "y1", "x2", "y2",
        "stroke-width", "gradientTransform", "patternTransform", "fx", "fy",
    };

    /// <summary>How far a rounded point may move, in CSS pixels at the size shown.</summary>
    public const double TolerancePx = 0.1;

    /// <summary>
    /// Decimals for coordinates when <paramref name="unitsPerPx"/> drawing units make one CSS pixel: rounding
    /// to d places moves a point at most half of 10^-d units, which must stay under <see cref="TolerancePx"/>.
    /// </summary>
    public static int DecimalsFor(double unitsPerPx)
    {
        if (!double.IsFinite(unitsPerPx) || unitsPerPx <= 0) return 4;
        var d = (int)Math.Ceiling(Math.Log10(0.5 / (TolerancePx * unitsPerPx)) - 1e-9);
        return Math.Clamp(d, 0, 4);
    }

    /// <param name="document">A sanitised SVG document (<see cref="SanitizedSvg.Document"/>).</param>
    /// <param name="viewBoxWidth">Its drawing's width, in its own units.</param>
    /// <param name="viewBoxHeight">Its drawing's height.</param>
    /// <param name="width">The largest width it's shown at, in CSS pixels.</param>
    /// <param name="height">The largest height it's shown at.</param>
    public static string Shrink(string document, double viewBoxWidth, double viewBoxHeight, double width, double height)
    {
        // Drawn with "meet": the side that fits tighter decides how many units make a pixel.
        var unitsPerPx = Math.Max(viewBoxWidth / width, viewBoxHeight / height);
        var decimals = DecimalsFor(unitsPerPx);
        var doc = XDocument.Parse(document);
        foreach (var el in doc.Root!.Descendants())
        {
            // A stroke drawn in screen pixels whatever the scale: its width isn't in drawing units.
            var fixedStroke = (string?)el.Attribute("vector-effect") == "non-scaling-stroke";
            foreach (var attr in el.Attributes().Where(a => a.Name.Namespace == XNamespace.None && Numeric.Contains(a.Name.LocalName)).ToList())
            {
                var name = attr.Name.LocalName;
                if (fixedStroke && name == "stroke-width") continue;
                attr.Value = name == "d"
                    ? PathData.Compact(attr.Value, decimals)
                    : ArtValues.Compact(attr.Value, name.EndsWith("ransform", StringComparison.Ordinal) ? Math.Max(decimals, 4) : decimals);
            }
        }
        return doc.Root.ToString(SaveOptions.DisableFormatting);
    }
}
