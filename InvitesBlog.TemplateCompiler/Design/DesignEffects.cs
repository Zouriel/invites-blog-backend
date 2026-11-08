namespace InvitesBlog.TemplateCompiler.Design;

/// <summary>
/// Layer effects on an element, as Photoshop's layer styles do them (<c>render/effects.ts</c> in the editor;
/// keep the two in step, byte for byte — the parity corpus compares them): a <b>bevel</b> (the edge lit
/// from the top left, raised or pressed in), an <b>inner glow</b> (a colour glowing in from the edges,
/// laid over in a blend mode), an <b>outer glow</b> (a colour glowing out around it, behind it) and a
/// <b>blend</b> mode for how the whole element mixes with what is under it (CSS mix-blend-mode).
///
/// <para>Bevel and glows are one SVG filter together with the element's pattern, applied in Photoshop's
/// order: a picture's duotone and grain, pattern, inner glow, bevel, then the drop shadow and outer glow
/// underneath. Everything here is built from
/// validated numbers, fixed names and resolved colours: nothing from the scene is pasted in.</para>
/// </summary>
public static class DesignEffects
{
    /// <summary>CSS blend modes: the same names in mix-blend-mode, feBlend and a canvas's composite operation.</summary>
    public static readonly IReadOnlyList<string> BlendModes =
    [
        "normal", "multiply", "screen", "overlay", "darken", "lighten", "color-dodge", "color-burn",
        "hard-light", "soft-light", "difference", "exclusion", "hue", "saturation", "color", "luminosity",
    ];
    public static readonly IReadOnlyList<string> BevelStyles = ["raised", "pressed"];
    public const double BevelMinSize = 1;
    public const double BevelMaxSize = 30;
    public const double GlowMinSize = 1;
    public const double GlowMaxSize = 60;
    public const double ShadowMaxOffset = 60;
    public const double ShadowMaxBlur = 60;

    public static bool IsBlendMode(string? mode) => mode is not null && BlendModes.Contains(mode);

    public sealed record Bevel(string Style, double Size, double Strength);
    public sealed record Glow(double Size, double Strength, string Blend);
    public sealed record Shadow(double X, double Y, double Blur, double Strength, string Colour);

    private static string N(double v) => DesignCss.Num(v);
    private static double StrengthOf(double v, double fallback) => double.IsFinite(v) ? Math.Min(1, Math.Max(0, v)) : fallback;
    private static double SizeOf(double v, double min, double max, double fallback) => double.IsFinite(v) ? Math.Min(max, Math.Max(min, v)) : fallback;

    /// <summary>A bevel as the compiler uses it, or null when there is none to draw.</summary>
    public static Bevel? ResolveBevel(DesignBevel? b)
    {
        if (b is null) return null;
        var strength = StrengthOf(b.Strength, 0.7);
        if (strength <= 0) return null;
        return new Bevel(b.Style == "pressed" ? "pressed" : "raised", SizeOf(b.Size, BevelMinSize, BevelMaxSize, 4), strength);
    }

    /// <summary>A glow as the compiler uses it, or null when there is none to draw.</summary>
    public static Glow? ResolveGlow(DesignGlow? g, string defaultBlend)
    {
        if (g is null) return null;
        var strength = StrengthOf(g.Strength, 0.75);
        if (strength <= 0) return null;
        return new Glow(SizeOf(g.Size, GlowMinSize, GlowMaxSize, 10), strength, IsBlendMode(g.Blend) ? g.Blend! : defaultBlend);
    }

    /// <summary>A shadow as the compiler uses it, or null when there is none to draw.</summary>
    public static Shadow? ResolveShadow(DesignShadow? sh, string colour)
    {
        if (sh is null) return null;
        var strength = StrengthOf(sh.Strength, 0.35);
        if (strength <= 0) return null;
        double Off(double v, double d) => double.IsFinite(v) ? Math.Min(ShadowMaxOffset, Math.Max(-ShadowMaxOffset, v)) : d;
        return new Shadow(Off(sh.X, 0), Off(sh.Y, 4), SizeOf(sh.Blur, 0, ShadowMaxBlur, 8), strength, colour);
    }

    /// <summary>A glow's spread: blurred alpha, doubled so the edge reads at full strength.</summary>
    private static string Spread(double size, string input, string result) =>
        $"<feGaussianBlur{(input.Length > 0 ? $" in=\"{input}\"" : "")} stdDeviation=\"{N(size / 2)}\"/><feComponentTransfer result=\"{result}\"><feFuncA type=\"linear\" slope=\"2\"/></feComponentTransfer>";

    /// <summary>
    /// The page's filter for an element's pattern and effects (canvas units for its box), or null when it
    /// has none. A pattern on its own is exactly the pattern's filter.
    /// </summary>
    public static string? Filter(string id, (DesignPatterns.Resolved P, string? Colour)? pattern, Bevel? bevel,
        (Glow G, string Colour)? inner, (Glow G, string Colour)? outer, double w, double h,
        (string Dark, string Light)? duotone = null, double grain = 0, Shadow? shadow = null, DesignPatterns.Light? light = null)
    {
        grain = double.IsFinite(grain) ? Math.Min(1, Math.Max(0, grain)) : 0;
        if (bevel is null && inner is null && outer is null && duotone is null && grain <= 0 && shadow is null)
            return pattern is { } only ? DesignPatterns.Filter(id, only.P, w, h, only.Colour, light) : null;

        var body = new System.Text.StringBuilder();
        var cur = "SourceGraphic";
        if (duotone is { } dt)
        {
            // Brightness becomes how much of the light colour shows over the dark one.
            body.Append("<feColorMatrix in=\"SourceGraphic\" type=\"luminanceToAlpha\" result=\"dl\"/>")
                .Append($"<feFlood style=\"flood-color:{dt.Dark}\"/><feComposite in2=\"SourceAlpha\" operator=\"in\" result=\"dd\"/>")
                .Append($"<feFlood style=\"flood-color:{dt.Light}\"/><feComposite in2=\"dl\" operator=\"in\"/><feComposite in2=\"SourceAlpha\" operator=\"in\"/>")
                .Append("<feComposite in2=\"dd\" operator=\"over\" result=\"dt\"/>");
            cur = "dt";
        }
        if (grain > 0)
        {
            // Grey noise, soft-lit over the picture and kept to it.
            body.Append("<feTurbulence type=\"fractalNoise\" baseFrequency=\"0.9\" numOctaves=\"2\" seed=\"11\"/>")
                .Append("<feColorMatrix values=\"0.33 0.33 0.33 0 0 0.33 0.33 0.33 0 0 0.33 0.33 0.33 0 0 0 0 0 0 1\"/>")
                .Append($"<feBlend in2=\"{cur}\" mode=\"soft-light\"/>")
                .Append($"<feComposite in2=\"{cur}\" operator=\"arithmetic\" k2=\"{N(grain)}\" k3=\"{N(1 - grain)}\"/>")
                .Append($"<feComposite in2=\"{cur}\" operator=\"in\" result=\"gr\"/>");
            cur = "gr";
        }
        if (pattern is { } pt)
        {
            var steps = DesignPatterns.Primitives(pt.P, w, h, pt.Colour, light).Replace("\"SourceGraphic\"", $"\"{cur}\"");
            body.Append(steps[..^2]).Append(" result=\"p\"/>");
            cur = "p";
        }
        if (inner is { } ig)
        {
            // The outside, blurred in over the edge and kept inside: a glow from the rim inwards.
            body.Append("<feComponentTransfer in=\"SourceAlpha\"><feFuncA type=\"table\" tableValues=\"1 0\"/></feComponentTransfer>")
                .Append(Spread(ig.G.Size, "", "ib")).Append("<feComposite in2=\"SourceAlpha\" operator=\"in\" result=\"ia\"/>")
                .Append($"<feFlood style=\"flood-color:{ig.Colour}\" flood-opacity=\"{N(ig.G.Strength)}\"/><feComposite in2=\"ia\" operator=\"in\"/>")
                .Append($"<feBlend in2=\"{cur}\" mode=\"{ig.G.Blend}\" result=\"ig\"/>");
            cur = "ig";
        }
        if (bevel is not null)
        {
            // The outline blurred into a slope and lit from the top left; grey where it is flat leaves the
            // colour as it is under hard light, so only the edge changes.
            var depth = bevel.Size * 1.25 * (bevel.Style == "pressed" ? -1 : 1);
            body.Append($"<feGaussianBlur in=\"SourceAlpha\" stdDeviation=\"{N(bevel.Size / 2)}\"/>")
                .Append(DesignPatterns.Lighting(null, depth, light, null))
                .Append($"<feBlend in2=\"{cur}\" mode=\"hard-light\"/>")
                .Append($"<feComposite in2=\"{cur}\" operator=\"arithmetic\" k2=\"{N(bevel.Strength)}\" k3=\"{N(1 - bevel.Strength)}\"/>")
                .Append($"<feComposite in2=\"{cur}\" operator=\"in\" result=\"bv\"/>");
            cur = "bv";
        }
        var under = new List<string>();
        if (shadow is not null)
        {
            body.Append($"<feGaussianBlur in=\"SourceAlpha\" stdDeviation=\"{N(shadow.Blur / 2)}\"/><feOffset dx=\"{N(shadow.X)}\" dy=\"{N(shadow.Y)}\" result=\"sa\"/>")
                .Append($"<feFlood style=\"flood-color:{shadow.Colour}\" flood-opacity=\"{N(shadow.Strength)}\"/><feComposite in2=\"sa\" operator=\"in\" result=\"sh\"/>");
            under.Add("sh");
        }
        if (outer is { } og)
        {
            body.Append(Spread(og.G.Size, "SourceAlpha", "oa"))
                .Append($"<feFlood style=\"flood-color:{og.Colour}\" flood-opacity=\"{N(og.G.Strength)}\"/><feComposite in2=\"oa\" operator=\"in\" result=\"og\"/>");
            under.Add("og");
        }
        if (under.Count > 0)
            body.Append("<feMerge>").Append(string.Concat(under.Select(r => $"<feMergeNode in=\"{r}\"/>"))).Append($"<feMergeNode in=\"{cur}\"/></feMerge>");
        // Room around the box for the glow (and for a blur that reaches past the edge), at least the
        // pattern's own margins.
        var reach = Math.Max(Math.Max(outer is { } o ? o.G.Size * 1.5 : 0, Math.Max(inner is { } i ? i.G.Size : 0, bevel?.Size ?? 0)),
            shadow is null ? 0 : Math.Max(Math.Abs(shadow.X), Math.Abs(shadow.Y)) + shadow.Blur * 1.5);
        var mx = Math.Max(10, 100 * reach / Math.Max(1, w));
        var my = Math.Max(25, 100 * reach / Math.Max(1, h));
        return $"<filter id=\"{id}\" x=\"{N(-mx)}%\" y=\"{N(-my)}%\" width=\"{N(100 + 2 * mx)}%\" height=\"{N(100 + 2 * my)}%\" color-interpolation-filters=\"sRGB\">{body}</filter>";
    }
}
