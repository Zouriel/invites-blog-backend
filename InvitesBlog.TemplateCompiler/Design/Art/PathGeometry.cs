using System.Globalization;

namespace InvitesBlog.TemplateCompiler.Design.Art;

/// <summary>
/// SVG path data flattened to polylines — enough geometry to walk <c>animateMotion</c> along a path by
/// distance and to measure a shape's box for <c>transform-box: fill-box</c>. Curves are cut into short
/// straight pieces; precision is a fraction of a unit, far below anything a phone draws.
/// </summary>
public sealed class PathGeometry
{
    private const int CurveSteps = 16;

    /// <summary>Each subpath as a run of points.</summary>
    public List<List<(double X, double Y)>> Polylines { get; } = new();

    public static PathGeometry Parse(string? d)
    {
        var geo = new PathGeometry();
        if (string.IsNullOrWhiteSpace(d)) return geo;
        var tokens = Tokenize(d);
        var i = 0;
        char cmd = ' ';
        double x = 0, y = 0, sx = 0, sy = 0;
        double lastCx = 0, lastCy = 0; // reflected control point for S/T
        char lastCmd = ' ';
        List<(double, double)>? line = null;

        double Next() => i < tokens.Count && tokens[i].IsNumber ? tokens[i++].Value : double.NaN;
        void Start(double px, double py)
        {
            line = new List<(double, double)> { (px, py) };
            geo.Polylines.Add(line);
        }
        void To(double px, double py)
        {
            if (line is null) Start(x, y);
            line!.Add((px, py));
        }

        var guard = 0;
        while (i < tokens.Count && guard++ < 200_000)
        {
            if (!tokens[i].IsNumber) cmd = tokens[i++].Command;
            else if (cmd is 'M') cmd = 'L';
            else if (cmd is 'm') cmd = 'l';
            else if (cmd == ' ') { i++; continue; }

            var rel = char.IsLower(cmd);
            var ox = rel ? x : 0;
            var oy = rel ? y : 0;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    var (px, py) = (Next() + ox, Next() + oy);
                    if (!double.IsFinite(px) || !double.IsFinite(py)) return geo;
                    x = sx = px; y = sy = py;
                    Start(x, y);
                    break;
                }
                case 'L':
                {
                    var (px, py) = (Next() + ox, Next() + oy);
                    if (!double.IsFinite(px) || !double.IsFinite(py)) return geo;
                    To(px, py); x = px; y = py;
                    break;
                }
                case 'H':
                {
                    var px = Next() + ox;
                    if (!double.IsFinite(px)) return geo;
                    To(px, y); x = px;
                    break;
                }
                case 'V':
                {
                    var py = Next() + oy;
                    if (!double.IsFinite(py)) return geo;
                    To(x, py); y = py;
                    break;
                }
                case 'C':
                case 'S':
                {
                    double x1, y1;
                    if (char.ToUpperInvariant(cmd) == 'C') { x1 = Next() + ox; y1 = Next() + oy; }
                    else if (char.ToUpperInvariant(lastCmd) is 'C' or 'S') { x1 = 2 * x - lastCx; y1 = 2 * y - lastCy; }
                    else { x1 = x; y1 = y; }
                    var (x2, y2, px, py) = (Next() + ox, Next() + oy, Next() + ox, Next() + oy);
                    if (!AllFinite(x1, y1, x2, y2, px, py)) return geo;
                    for (var k = 1; k <= CurveSteps; k++)
                    {
                        var t = (double)k / CurveSteps;
                        var u = 1 - t;
                        To(u * u * u * x + 3 * u * u * t * x1 + 3 * u * t * t * x2 + t * t * t * px,
                           u * u * u * y + 3 * u * u * t * y1 + 3 * u * t * t * y2 + t * t * t * py);
                    }
                    lastCx = x2; lastCy = y2; x = px; y = py;
                    break;
                }
                case 'Q':
                case 'T':
                {
                    double x1, y1;
                    if (char.ToUpperInvariant(cmd) == 'Q') { x1 = Next() + ox; y1 = Next() + oy; }
                    else if (char.ToUpperInvariant(lastCmd) is 'Q' or 'T') { x1 = 2 * x - lastCx; y1 = 2 * y - lastCy; }
                    else { x1 = x; y1 = y; }
                    var (px, py) = (Next() + ox, Next() + oy);
                    if (!AllFinite(x1, y1, px, py)) return geo;
                    for (var k = 1; k <= CurveSteps; k++)
                    {
                        var t = (double)k / CurveSteps;
                        var u = 1 - t;
                        To(u * u * x + 2 * u * t * x1 + t * t * px, u * u * y + 2 * u * t * y1 + t * t * py);
                    }
                    lastCx = x1; lastCy = y1; x = px; y = py;
                    break;
                }
                case 'A':
                {
                    var (rx, ry, rot) = (Next(), Next(), Next());
                    var (large, sweep) = (Next(), Next());
                    var (px, py) = (Next() + ox, Next() + oy);
                    if (!AllFinite(rx, ry, rot, large, sweep, px, py)) return geo;
                    foreach (var p in Arc(x, y, rx, ry, rot, large != 0, sweep != 0, px, py)) To(p.X, p.Y);
                    x = px; y = py;
                    break;
                }
                case 'Z':
                    To(sx, sy);
                    x = sx; y = sy;
                    line = null;
                    break;
                default:
                    i++;
                    break;
            }
            lastCmd = cmd;
            if (char.ToUpperInvariant(cmd) == 'Z') cmd = ' ';
        }
        return geo;
    }

    private static bool AllFinite(params double[] values) => values.All(double.IsFinite);

    /// <summary>An elliptical arc as points, by SVG's endpoint-to-centre conversion (F.6.5).</summary>
    private static IEnumerable<(double X, double Y)> Arc(
        double x1, double y1, double rx, double ry, double degrees, bool large, bool sweep, double x2, double y2)
    {
        if (rx == 0 || ry == 0 || x1 == x2 && y1 == y2)
        {
            yield return (x2, y2);
            yield break;
        }
        rx = Math.Abs(rx); ry = Math.Abs(ry);
        var phi = degrees * Math.PI / 180;
        var (cos, sin) = (Math.Cos(phi), Math.Sin(phi));
        var dx = (x1 - x2) / 2;
        var dy = (y1 - y2) / 2;
        var x1p = cos * dx + sin * dy;
        var y1p = -sin * dx + cos * dy;
        var lambda = x1p * x1p / (rx * rx) + y1p * y1p / (ry * ry);
        if (lambda > 1) { rx *= Math.Sqrt(lambda); ry *= Math.Sqrt(lambda); }
        var num = rx * rx * ry * ry - rx * rx * y1p * y1p - ry * ry * x1p * x1p;
        var den = rx * rx * y1p * y1p + ry * ry * x1p * x1p;
        var coef = den == 0 ? 0 : Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
        var cxp = coef * rx * y1p / ry;
        var cyp = -coef * ry * x1p / rx;
        var cx = cos * cxp - sin * cyp + (x1 + x2) / 2;
        var cy = sin * cxp + cos * cyp + (y1 + y2) / 2;
        static double Angle(double ux, double uy, double vx, double vy)
        {
            var a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
            return a;
        }
        var theta = Angle(1, 0, (x1p - cxp) / rx, (y1p - cyp) / ry);
        var delta = Angle((x1p - cxp) / rx, (y1p - cyp) / ry, (-x1p - cxp) / rx, (-y1p - cyp) / ry);
        if (!sweep && delta > 0) delta -= 2 * Math.PI;
        else if (sweep && delta < 0) delta += 2 * Math.PI;
        var steps = Math.Max(4, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 16)));
        for (var k = 1; k <= steps; k++)
        {
            var a = theta + delta * k / steps;
            var (ca, sa) = (Math.Cos(a), Math.Sin(a));
            yield return (cos * rx * ca - sin * ry * sa + cx, sin * rx * ca + cos * ry * sa + cy);
        }
    }

    private readonly record struct Token(bool IsNumber, double Value, char Command);

    private static List<Token> Tokenize(string d)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < d.Length)
        {
            var c = d[i];
            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
            if ("MmLlHhVvCcSsQqTtAaZz".Contains(c))
            {
                tokens.Add(new Token(false, 0, c));
                i++;
                continue;
            }
            // A number: sign, digits, one dot, exponent. "1.5.5" is two numbers; "-1-2" is two.
            var start = i;
            if (c is '+' or '-') i++;
            var dot = false;
            while (i < d.Length && (char.IsAsciiDigit(d[i]) || d[i] == '.' && !dot))
            {
                if (d[i] == '.') dot = true;
                i++;
            }
            if (i < d.Length && d[i] is 'e' or 'E')
            {
                var save = i;
                i++;
                if (i < d.Length && d[i] is '+' or '-') i++;
                if (i < d.Length && char.IsAsciiDigit(d[i])) while (i < d.Length && char.IsAsciiDigit(d[i])) i++;
                else i = save;
            }
            if (i == start) { i++; continue; }
            if (double.TryParse(d.AsSpan(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                tokens.Add(new Token(true, v, ' '));
        }
        return tokens;
    }

    // ----- Measuring --------------------------------------------------------------------------------

    public double Length => Polylines.Sum(PolylineLength);

    private static double PolylineLength(List<(double X, double Y)> line)
    {
        double sum = 0;
        for (var k = 1; k < line.Count; k++) sum += Distance(line[k - 1], line[k]);
        return sum;
    }

    private static double Distance((double X, double Y) a, (double X, double Y) b) =>
        Math.Sqrt((b.X - a.X) * (b.X - a.X) + (b.Y - a.Y) * (b.Y - a.Y));

    /// <summary>
    /// The point a fraction of the way along the whole path by distance, with the direction of travel
    /// there in degrees (what <c>rotate="auto"</c> turns to).
    /// </summary>
    public (double X, double Y, double Degrees) At(double fraction)
    {
        var total = Length;
        var points = Polylines.Where(l => l.Count > 0).ToList();
        if (points.Count == 0) return (0, 0, 0);
        if (total <= 0) return (points[0][0].X, points[0][0].Y, 0);
        var target = Math.Clamp(fraction, 0, 1) * total;
        double walked = 0;
        (double X, double Y, double Degrees) last = (points[0][0].X, points[0][0].Y, 0);
        foreach (var line in points)
        {
            for (var k = 1; k < line.Count; k++)
            {
                var seg = Distance(line[k - 1], line[k]);
                if (seg <= 0) continue;
                var deg = Math.Atan2(line[k].Y - line[k - 1].Y, line[k].X - line[k - 1].X) * 180 / Math.PI;
                if (walked + seg >= target)
                {
                    var u = (target - walked) / seg;
                    return (line[k - 1].X + (line[k].X - line[k - 1].X) * u, line[k - 1].Y + (line[k].Y - line[k - 1].Y) * u, deg);
                }
                walked += seg;
                last = (line[k].X, line[k].Y, deg);
            }
        }
        return last;
    }

    /// <summary>The box around every point, or null for an empty path.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY)? Bounds()
    {
        var all = Polylines.SelectMany(l => l).ToList();
        if (all.Count == 0) return null;
        return (all.Min(p => p.X), all.Min(p => p.Y), all.Max(p => p.X), all.Max(p => p.Y));
    }
}
