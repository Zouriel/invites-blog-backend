namespace InvitesBlog.TemplateCompiler.Design;

/// <summary>
/// Gradients that paint what an element draws (<c>render/gradient.ts</c> in the editor; keep the two in
/// step, byte for byte). Colours come in resolved (hex, or var(--ib-key) for a theme colour, so a host's
/// recolouring still reaches them). A shape gets an SVG gradient inside its drawing; text, buttons,
/// pictures and illustrations get the same gradient as CSS.
/// </summary>
public static class DesignGradients
{
    public static readonly IReadOnlyList<string> Types = ["linear", "radial"];
    public const int MaxStops = 6;

    public sealed record Stop(string Color, double At);
    public sealed record Resolved(string Type, double Angle, IReadOnlyList<Stop> Stops);

    private static string N(double v) => DesignCss.Num(v);

    /// <summary>A gradient as the compiler uses it, or null when it hasn't two colours that resolve.</summary>
    public static Resolved? Resolve(DesignGradient? g, Func<string?, string?> resolve)
    {
        if (g is null) return null;
        var kept = g.Stops.Take(MaxStops).Select(s => (Color: resolve(s.Color), s.At)).Where(s => s.Color is not null).ToList();
        if (kept.Count < 2) return null;
        var last = kept.Count - 1;
        var stops = kept.Select((s, i) => new Stop(s.Color!,
            s.At is { } at && double.IsFinite(at) ? Math.Min(1, Math.Max(0, at)) : (double)i / last)).ToList();
        var angle = double.IsFinite(g.Angle) ? ((g.Angle % 360) + 360) % 360 : 180;
        return new Resolved(g.Type == "radial" ? "radial" : "linear", angle, stops);
    }

    /// <summary>The gradient as a CSS image.</summary>
    public static string Css(Resolved g)
    {
        var stops = string.Join(",", g.Stops.Select(s => $"{s.Color} {N(s.At * 100)}%"));
        return g.Type == "radial" ? $"radial-gradient(ellipse farthest-corner,{stops})" : $"linear-gradient({N(g.Angle)}deg,{stops})";
    }

    /// <summary>The gradient as an SVG paint server with an id, for a drawing whose viewBox is w × h.</summary>
    public static string Svg(string id, Resolved g, double w, double h)
    {
        var stops = string.Concat(g.Stops.Select(s => $"<stop offset=\"{N(s.At)}\" style=\"stop-color:{s.Color}\"/>"));
        if (g.Type == "radial")
            return $"<radialGradient id=\"{id}\" cx=\"0.5\" cy=\"0.5\" r=\"0.7071\">{stops}</radialGradient>";
        var rad = g.Angle * Math.PI / 180;
        double dx = Math.Sin(rad), dy = -Math.Cos(rad);
        var half = (Math.Abs(w * dx) + Math.Abs(h * dy)) / 2;
        double cx = w / 2, cy = h / 2;
        return $"<linearGradient id=\"{id}\" gradientUnits=\"userSpaceOnUse\" x1=\"{N(cx - dx * half)}\" y1=\"{N(cy - dy * half)}\" x2=\"{N(cx + dx * half)}\" y2=\"{N(cy + dy * half)}\">{stops}</linearGradient>";
    }
}
