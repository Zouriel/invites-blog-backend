namespace InvitesBlog.TemplateCompiler.Design;

/// <summary>
/// Ready-made shapes with a setting or two (<c>render/shapes.ts</c> in the editor; keep the two in step,
/// byte for byte — the parity corpus compares them). Each is an SVG path in a box of the element's
/// size; the same outlines clip pictures and groups (masks). <c>sides</c> is the count (a star's points,
/// a badge's scallops, a divider's waves or teeth) and <c>amount</c> the one shape-specific setting, 0–1.
/// </summary>
public static class DesignShapes
{
    public static readonly IReadOnlyList<string> ExtraKinds = ["star", "heart", "arch", "ribbon", "bubble", "arrow", "scallop", "wave", "zigzag", "frame", "ticket"];
    /// <summary>Outlines a picture, illustration or group can be clipped to.</summary>
    public static readonly IReadOnlyList<string> MaskKinds = ["ellipse", "rect", "polygon", "star", "heart", "arch", "scallop", "ticket", "bubble", "ribbon", "arrow"];

    public static bool IsExtra(string? kind) => kind is not null && ExtraKinds.Contains(kind);
    public static bool IsMaskKind(string? kind) => kind is not null && MaskKinds.Contains(kind);

    private static readonly Dictionary<string, double> Amounts = new(StringComparer.Ordinal)
    {
        ["star"] = 0.5, ["ribbon"] = 0.5, ["bubble"] = 0.3, ["arrow"] = 0.45, ["scallop"] = 0.12, ["wave"] = 0.35,
        ["zigzag"] = 0.35, ["frame"] = 0.15, ["ticket"] = 0.25, ["rect"] = 0.2,
    };
    private static readonly Dictionary<string, (int Min, int Max)> Counts = new(StringComparer.Ordinal)
    {
        ["polygon"] = (3, 12), ["star"] = (3, 24), ["scallop"] = (6, 40), ["wave"] = (1, 12), ["zigzag"] = (2, 40),
    };

    private static string N(double v) => DesignCss.Num(v);

    /// <summary>The kind's setting, held to 0–1 (unset or not a number: the default).</summary>
    public static double AmountOf(string kind, double? amount)
    {
        var d = Amounts.TryGetValue(kind, out var v) ? v : 0.5;
        return amount is not { } a || !double.IsFinite(a) ? d : Math.Min(1, Math.Max(0, a));
    }

    /// <summary>The kind's count, held to its range.</summary>
    public static int CountOf(string kind, int sides) =>
        Counts.TryGetValue(kind, out var c) ? Math.Min(c.Max, Math.Max(c.Min, sides)) : 0;

    /// <summary>
    /// The outline of a shape kind in a w × h box, <paramref name="inset"/> in from every edge, as an SVG
    /// path; null for a kind this doesn't draw.
    /// </summary>
    public static string? D(string kind, double w, double h, int sides, double? amount, double radius, double inset = 0)
    {
        double x0 = inset, y0 = inset, x1 = Math.Max(inset, w - inset), y1 = Math.Max(inset, h - inset);
        double W = x1 - x0, H = y1 - y0, cx = x0 + W / 2, cy = y0 + H / 2, rx = W / 2, ry = H / 2;
        var a = AmountOf(kind, amount);
        string P(double x, double y) => N(x) + " " + N(y);
        string Rounded(double r, double ax, double ay, double bx, double by)
        {
            var q = Math.Max(0, Math.Min(r, Math.Min((bx - ax) / 2, (by - ay) / 2)));
            if (q <= 0) return $"M{P(ax, ay)}H{N(bx)}V{N(by)}H{N(ax)}Z";
            return $"M{P(ax + q, ay)}H{N(bx - q)}A{N(q)} {N(q)} 0 0 1 {P(bx, ay + q)}V{N(by - q)}A{N(q)} {N(q)} 0 0 1 {P(bx - q, by)}"
                + $"H{N(ax + q)}A{N(q)} {N(q)} 0 0 1 {P(ax, by - q)}V{N(ay + q)}A{N(q)} {N(q)} 0 0 1 {P(ax + q, ay)}Z";
        }
        string Ring(Func<int, double> radiusAt, int steps)
        {
            var d = new System.Text.StringBuilder();
            for (var k = 0; k < steps; k++)
            {
                var t = -Math.PI / 2 + k * 2 * Math.PI / steps;
                var r = radiusAt(k);
                d.Append(k == 0 ? 'M' : 'L').Append(P(cx + rx * r * Math.Cos(t), cy + ry * r * Math.Sin(t)));
            }
            return d.Append('Z').ToString();
        }
        switch (kind)
        {
            case "rect": return Rounded(a * Math.Min(W, H) / 2, x0, y0, x1, y1);
            case "ellipse": return $"M{P(x0, cy)}A{N(rx)} {N(ry)} 0 1 1 {P(x1, cy)}A{N(rx)} {N(ry)} 0 1 1 {P(x0, cy)}Z";
            case "polygon": { var c = CountOf("polygon", sides); return Ring(_ => 1, c); }
            case "star":
            {
                var c = CountOf("star", sides);
                var inner = Math.Min(0.95, Math.Max(0.05, 1 - a * 0.9));
                return Ring(k => k % 2 == 0 ? 1 : inner, c * 2);
            }
            case "heart":
            {
                string U(double u, double v) => P(x0 + W * u, y0 + H * v);
                return $"M{U(0.5, 0.25)}C{U(0.5, 0.1)} {U(0.35, 0)} {U(0.22, 0)}C{U(0.08, 0)} {U(0, 0.12)} {U(0, 0.28)}"
                    + $"C{U(0, 0.52)} {U(0.25, 0.7)} {U(0.5, 1)}C{U(0.75, 0.7)} {U(1, 0.52)} {U(1, 0.28)}"
                    + $"C{U(1, 0.12)} {U(0.92, 0)} {U(0.78, 0)}C{U(0.65, 0)} {U(0.5, 0.1)} {U(0.5, 0.25)}Z";
            }
            case "arch":
            {
                var t = Math.Min(rx, H);
                return $"M{P(x0, y1)}V{N(y0 + t)}A{N(rx)} {N(t)} 0 0 1 {P(x1, y0 + t)}V{N(y1)}Z";
            }
            case "ribbon":
            {
                var d = a * Math.Min(W / 2, H);
                return $"M{P(x0, y0)}H{N(x1)}L{P(x1 - d, cy)}L{P(x1, y1)}H{N(x0)}L{P(x0 + d, cy)}Z";
            }
            case "bubble":
            {
                var yb = y0 + H * 0.8;
                var q = Math.Max(0, Math.Min(radius, Math.Min(W / 2, (yb - y0) / 2)));
                var tx = x0 + W * (0.1 + 0.8 * a);
                var tw = Math.Min(W * 0.12, yb - y0);
                double left = Math.Max(x0 + q, tx - tw / 2), right = Math.Min(x1 - q, tx + tw / 2);
                return $"M{P(x0 + q, y0)}H{N(x1 - q)}A{N(q)} {N(q)} 0 0 1 {P(x1, y0 + q)}V{N(yb - q)}A{N(q)} {N(q)} 0 0 1 {P(x1 - q, yb)}"
                    + $"H{N(right)}L{P(tx - tw / 2, y1)}L{P(left, yb)}H{N(x0 + q)}A{N(q)} {N(q)} 0 0 1 {P(x0, yb - q)}V{N(y0 + q)}A{N(q)} {N(q)} 0 0 1 {P(x0 + q, y0)}Z";
            }
            case "arrow":
            {
                var t = H * Math.Min(0.95, Math.Max(0.1, a));
                var hl = Math.Min(W * 0.45, H * 0.8);
                return $"M{P(x0, cy - t / 2)}H{N(x1 - hl)}V{N(y0)}L{P(x1, cy)}L{P(x1 - hl, y1)}V{N(cy + t / 2)}H{N(x0)}Z";
            }
            case "scallop":
            {
                var c = CountOf("scallop", sides);
                var depth = Math.Min(0.4, Math.Max(0.02, a));
                var valley = 1 - 2 * depth;
                var control = 2 - valley * Math.Cos(Math.PI / c);
                var d = new System.Text.StringBuilder();
                for (var k = 0; k <= c; k++)
                {
                    var t = -Math.PI / 2 + k * 2 * Math.PI / c;
                    var v = P(cx + rx * valley * Math.Cos(t), cy + ry * valley * Math.Sin(t));
                    if (k == 0) { d.Append('M').Append(v); continue; }
                    var m = t - Math.PI / c;
                    d.Append('Q').Append(P(cx + rx * control * Math.Cos(m), cy + ry * control * Math.Sin(m))).Append(' ').Append(v);
                }
                return d.Append('Z').ToString();
            }
            case "wave":
            {
                var c = CountOf("wave", sides);
                var A = H * Math.Min(1, Math.Max(0.02, a)) / 2;
                var yb = y0 + A;
                var ww = W / c;
                var d = new System.Text.StringBuilder($"M{P(x0, yb)}Q{P(x0 + ww / 4, yb - 2 * A)} {P(x0 + ww / 2, yb)}");
                for (var k = 2; k <= 2 * c; k++) d.Append('T').Append(P(x0 + k * ww / 2, yb));
                return d.Append($"V{N(y1)}H{N(x0)}Z").ToString();
            }
            case "zigzag":
            {
                var c = CountOf("zigzag", sides);
                var A = H * Math.Min(1, Math.Max(0.02, a));
                var tw = W / c;
                var d = new System.Text.StringBuilder($"M{P(x0, y0 + A)}");
                for (var k = 0; k < c; k++) d.Append('L').Append(P(x0 + (k + 0.5) * tw, y0)).Append('L').Append(P(x0 + (k + 1) * tw, y0 + A));
                return d.Append($"V{N(y1)}H{N(x0)}Z").ToString();
            }
            case "frame":
            {
                var t = Math.Max(1, a * Math.Min(W, H) / 2);
                return Rounded(radius, x0, y0, x1, y1) + Rounded(Math.Max(0, radius - t), x0 + t, y0 + t, x1 - t, y1 - t);
            }
            case "ticket":
            {
                var nr = Math.Min(Math.Min(a * H / 2, H / 2 - 1), W / 4);
                var q = Math.Max(0, Math.Min(radius, Math.Min(W / 2, H / 2 - nr)));
                var nn = Math.Max(0, nr);
                return $"M{P(x0 + q, y0)}H{N(x1 - q)}A{N(q)} {N(q)} 0 0 1 {P(x1, y0 + q)}V{N(cy - nn)}A{N(nn)} {N(nn)} 0 0 0 {P(x1, cy + nn)}"
                    + $"V{N(y1 - q)}A{N(q)} {N(q)} 0 0 1 {P(x1 - q, y1)}H{N(x0 + q)}A{N(q)} {N(q)} 0 0 1 {P(x0, y1 - q)}"
                    + $"V{N(cy + nn)}A{N(nn)} {N(nn)} 0 0 0 {P(x0, cy - nn)}V{N(y0 + q)}A{N(q)} {N(q)} 0 0 1 {P(x0 + q, y0)}Z";
            }
            default: return null;
        }
    }
}
