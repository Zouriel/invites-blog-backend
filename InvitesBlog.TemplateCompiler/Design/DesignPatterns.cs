using System.Text;

namespace InvitesBlog.TemplateCompiler.Design;

/// <summary>
/// Patterns laid over whatever an element draws (<c>render/patterns.ts</c> in the editor; keep the two in
/// step, byte for byte — the parity corpus compares them).
///
/// <para>A pattern is a map of marks (where the clouds, dots or stripes are) used the way Photoshop's
/// Pattern Overlay or a texture layer is: the marks are clipped to the element's own outline, so only what
/// it paints gets them, and they are applied in one of five ways: <b>shade</b> (darken), <b>shine</b>
/// (lighten), <b>colour</b> (a second colour), <b>emboss</b> and <b>engrave</b> (raised or pressed in, lit
/// from the top left).</para>
///
/// <para>Textures are Perlin noise made by the browser (feTurbulence) with a contrast curve; the geometric
/// ones are a tiled SVG drawn once over the element's box. Everything here is built from validated numbers
/// and fixed strings: nothing from the scene is pasted in.</para>
/// </summary>
public static class DesignPatterns
{
    public static readonly IReadOnlyList<string> Textures = ["clouds", "marble", "paper", "linen", "sand", "watercolour", "wood"];
    public static readonly IReadOnlyList<string> Shapes = ["dots", "stripes", "pinstripe", "gingham", "grid", "chevron", "diamonds", "waves", "hearts", "stars"];
    public static readonly IReadOnlyList<string> Kinds = [.. Textures, .. Shapes];
    public static readonly IReadOnlyList<string> Modes = ["shade", "shine", "colour", "emboss", "engrave"];
    public const double MinSize = 0.25;
    public const double MaxSize = 4;

    public static bool IsKind(string? kind) => kind is not null && Kinds.Contains(kind);
    private static bool IsTexture(string kind) => Textures.Contains(kind);

    private static string N(double v) => DesignCss.Num(v);

    /// <summary>A frequency given per thousand units, at a size: exact in either language, however small.</summary>
    private static string Freq(double perThousand, double size) => N(perThousand / size) + "e-3";

    private static string Matrix(double gain, double offset) =>
        $"<feColorMatrix values=\"0 0 0 0 0 0 0 0 0 0 0 0 0 0 0 {N(gain)} 0 0 0 {N(offset)}\" result=\"m\"/>";

    /// <summary>The filter steps that leave a texture's marks as the alpha of result "m" (black elsewhere).</summary>
    public static string TexturePrimitives(string kind, double size)
    {
        string Turb(string type, double fx, double fy, int octaves, int seed, string result = "") =>
            $"<feTurbulence type=\"{type}\" baseFrequency=\"{Freq(fx, size)}{(fy == fx ? "" : " " + Freq(fy, size))}\" numOctaves=\"{octaves}\" seed=\"{seed}\"{(result.Length > 0 ? $" result=\"{result}\"" : "")}/>";
        return kind switch
        {
            "clouds" => Turb("fractalNoise", 9, 9, 4, 2) + Matrix(2.8, -1.05),
            "marble" => Turb("turbulence", 10, 30, 5, 5) + Matrix(-6, 1.45),
            "paper" => Turb("fractalNoise", 700, 700, 3, 1) + Matrix(2.2, -0.85),
            "linen" => Turb("fractalNoise", 500, 25, 2, 3, "h") + Turb("fractalNoise", 25, 500, 2, 8, "v")
                + "<feComposite in=\"h\" in2=\"v\" operator=\"arithmetic\" k2=\"0.5\" k3=\"0.5\"/>" + Matrix(4, -1.7),
            "sand" => Turb("fractalNoise", 450, 450, 1, 4) + Matrix(10, -6),
            "watercolour" => Turb("fractalNoise", 12, 12, 5, 7) + Matrix(4.5, -1.9),
            _ => Turb("fractalNoise", 6, 140, 3, 6) + Matrix(3.4, -1.5), // wood
        };
    }

    /// <summary>One tile of a geometric pattern at a size: its width, height, rotation and black marks.</summary>
    private static (double W, double H, bool Turn, string Marks) Tile(string kind, double size)
    {
        var s = 24 * size;
        switch (kind)
        {
            case "dots":
                return (s, s, false, $"<circle cx=\"{N(s / 4)}\" cy=\"{N(s / 4)}\" r=\"{N(s * 0.1)}\"/><circle cx=\"{N(s * 0.75)}\" cy=\"{N(s * 0.75)}\" r=\"{N(s * 0.1)}\"/>");
            case "stripes":
                return (s, s, true, $"<rect width=\"{N(s * 0.35)}\" height=\"{N(s)}\"/>");
            case "pinstripe":
                return (s, s, false, $"<rect width=\"{N(Math.Max(1, s * 0.05))}\" height=\"{N(s)}\"/>");
            case "gingham":
                return (s, s, false, $"<rect width=\"{N(s)}\" height=\"{N(s / 2)}\" fill-opacity=\"0.5\"/><rect width=\"{N(s / 2)}\" height=\"{N(s)}\" fill-opacity=\"0.5\"/>");
            case "grid":
            {
                var lw = N(Math.Max(1, s * 0.04));
                return (s, s, false, $"<rect width=\"{N(s)}\" height=\"{lw}\"/><rect width=\"{lw}\" height=\"{N(s)}\"/>");
            }
            case "chevron":
            {
                var h = s / 2;
                string lo = N(h * 0.8), hi = N(h * 0.2);
                return (s, h, false, $"<path d=\"M{N(-s / 2)} {lo}L0 {hi}L{N(s / 2)} {lo}L{N(s)} {hi}L{N(s * 1.5)} {lo}\" fill=\"none\" stroke=\"#000\" stroke-width=\"{N(s * 0.08)}\"/>");
            }
            case "diamonds":
                return (s, s, false, $"<path d=\"M{N(s / 2)} 0L{N(s)} {N(s / 2)}L{N(s / 2)} {N(s)}L0 {N(s / 2)}Z\" fill=\"none\" stroke=\"#000\" stroke-width=\"{N(s * 0.05)}\"/>");
            case "waves":
            {
                var h = s / 2;
                var mid = N(h / 2);
                return (s, h, false, $"<path d=\"M0 {mid}Q{N(s / 4)} 0 {N(s / 2)} {mid}T{N(s)} {mid}\" fill=\"none\" stroke=\"#000\" stroke-width=\"{N(s * 0.06)}\"/>");
            }
            case "hearts":
                return (s, s, false, $"<path transform=\"translate({N(s / 2)} {N(s / 2)}) scale({N(s / 55)})\" d=\"M0 9C-18-3-9-18 0-7C9-18 18-3 0 9Z\"/>");
            default: // stars
                return (s, s, false, $"<path transform=\"translate({N(s / 2)} {N(s / 2)}) scale({N(s / 42)})\" d=\"M0-10L2.9-4L9.5-3.1L4.8 1.5L5.9 8.1L0 5L-5.9 8.1L-4.8 1.5L-9.5-3.1L-2.9-4Z\"/>");
        }
    }

    /// <summary>A standalone SVG of a geometric pattern's marks over a box (canvas units): black on transparent.</summary>
    public static string ShapeMarksSvg(string kind, double size, double x, double y, double w, double h)
    {
        var t = Tile(kind, size);
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(w)}\" height=\"{N(h)}\" viewBox=\"{N(x)} {N(y)} {N(w)} {N(h)}\">"
            + $"<defs><pattern id=\"p\" width=\"{N(t.W)}\" height=\"{N(t.H)}\" patternUnits=\"userSpaceOnUse\"{(t.Turn ? " patternTransform=\"rotate(45)\"" : "")}>{t.Marks}</pattern></defs>"
            + $"<rect x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(w)}\" height=\"{N(h)}\" fill=\"url(#p)\"/></svg>";
    }

    public sealed record Resolved(string Kind, string Mode, double Size, double Strength);

    /// <summary>A pattern as the compiler uses it, or null when there is none to draw.</summary>
    public static Resolved? Resolve(DesignPattern? p)
    {
        if (p is null || !IsKind(p.Kind)) return null;
        var strength = double.IsFinite(p.Strength) ? Math.Min(1, Math.Max(0, p.Strength)) : 0.5;
        if (strength <= 0) return null;
        var size = double.IsFinite(p.Size) ? Math.Min(MaxSize, Math.Max(MinSize, p.Size)) : 1;
        var mode = p.Mode is not null && Modes.Contains(p.Mode) ? p.Mode : "shade";
        return new Resolved(p.Kind!, mode, size, strength);
    }

    /// <summary>
    /// The page's filter for a pattern on an element of a size (canvas units). <paramref name="colour"/> is
    /// a CSS colour (hex or var()) for the "colour" way; the others pick their own.
    /// </summary>
    public static string Filter(string id, Resolved p, double w, double h, string? colour) =>
        $"<filter id=\"{id}\" x=\"-10%\" y=\"-25%\" width=\"120%\" height=\"150%\" color-interpolation-filters=\"sRGB\">{Primitives(p, w, h, colour)}</filter>";

    /// <summary>The filter steps of a pattern over SourceGraphic, the last one being the patterned result.</summary>
    public static string Primitives(Resolved p, double w, double h, string? colour)
    {
        string marks;
        if (IsTexture(p.Kind)) marks = TexturePrimitives(p.Kind, p.Size);
        else
        {
            // Drawn over more than the box: a phone wider than 390 shows the element bigger, and the
            // filter reaches a little past the box for anything that overflows it.
            double W = Math.Max(1, w), H = Math.Max(1, h);
            double x = -W / 4, y = -H / 4, bw = W * 1.75, bh = H * 1.75;
            var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(ShapeMarksSvg(p.Kind, p.Size, x, y, bw, bh)));
            marks = $"<feImage href=\"data:image/svg+xml;base64,{data}\" x=\"{N(x)}\" y=\"{N(y)}\" width=\"{N(bw)}\" height=\"{N(bh)}\" preserveAspectRatio=\"none\" result=\"m\"/>";
        }
        string apply;
        if (p.Mode is "emboss" or "engrave")
        {
            var depth = (IsTexture(p.Kind) ? 10 : 3) * (p.Mode == "emboss" ? 1 : -1);
            apply = $"<feDiffuseLighting in=\"m\" surfaceScale=\"{N(depth)}\" diffuseConstant=\"0.7071\" lighting-color=\"#fff\" result=\"l\"><feDistantLight azimuth=\"225\" elevation=\"45\"/></feDiffuseLighting>"
                + "<feBlend in=\"l\" in2=\"SourceGraphic\" mode=\"hard-light\"/>"
                + $"<feComposite in2=\"SourceGraphic\" operator=\"arithmetic\" k2=\"{N(p.Strength)}\" k3=\"{N(1 - p.Strength)}\"/>"
                + "<feComposite in2=\"SourceAlpha\" operator=\"in\"/>";
        }
        else
        {
            var c = p.Mode == "shine" ? "#ffffff" : p.Mode == "colour" && colour is not null ? colour : "#000000";
            apply = $"<feFlood style=\"flood-color:{c}\" flood-opacity=\"{N(p.Strength)}\"/><feComposite in2=\"m\" operator=\"in\"/><feComposite in2=\"SourceGraphic\" operator=\"atop\"/>";
        }
        return marks + apply;
    }
}
