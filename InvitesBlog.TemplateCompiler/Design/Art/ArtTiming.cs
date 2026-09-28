using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InvitesBlog.TemplateCompiler.Design.Art;

/// <summary>An easing curve (CSS timing function, or a SMIL key spline).</summary>
public sealed class Easing
{
    private readonly Func<double, double> fn;
    private Easing(Func<double, double> fn) => this.fn = fn;

    public static readonly Easing Linear = new(x => x);

    public double Apply(double x) => fn(Math.Clamp(x, 0, 1));

    private static readonly Regex BezierRegex = new(@"^cubic-bezier\(([^)]*)\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex StepsRegex = new(@"^steps\(\s*(\d+)\s*(?:,\s*([a-z-]+))?\s*\)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>A CSS timing function; unknown ones are CSS's default, <c>ease</c>.</summary>
    public static Easing Parse(string? css)
    {
        var v = (css ?? "ease").Trim().ToLowerInvariant();
        switch (v)
        {
            case "linear": return Linear;
            case "ease": return Bezier(0.25, 0.1, 0.25, 1);
            case "ease-in": return Bezier(0.42, 0, 1, 1);
            case "ease-out": return Bezier(0, 0, 0.58, 1);
            case "ease-in-out": return Bezier(0.42, 0, 0.58, 1);
            case "step-start": return Steps(1, "start");
            case "step-end": return Steps(1, "end");
        }
        var b = BezierRegex.Match(v);
        if (b.Success)
        {
            var n = Affine.Numbers(b.Groups[1].Value);
            if (n.Count == 4) return Bezier(n[0], n[1], n[2], n[3]);
        }
        var s = StepsRegex.Match(v);
        if (s.Success && int.TryParse(s.Groups[1].Value, out var count) && count > 0)
            return Steps(Math.Min(count, 1000), s.Groups[2].Success ? s.Groups[2].Value : "end");
        return Bezier(0.25, 0.1, 0.25, 1);
    }

    public static Easing Steps(int count, string position)
    {
        var start = position is "start" or "jump-start";
        return new Easing(x =>
        {
            var step = Math.Floor(x * count + (start ? 1 : 0));
            return Math.Clamp(step / count, 0, 1);
        });
    }

    public static Easing Bezier(double x1, double y1, double x2, double y2)
    {
        x1 = Math.Clamp(x1, 0, 1);
        x2 = Math.Clamp(x2, 0, 1);
        double Coord(double t, double p1, double p2) => 3 * (1 - t) * (1 - t) * t * p1 + 3 * (1 - t) * t * t * p2 + t * t * t;
        return new Easing(x =>
        {
            if (x <= 0) return 0;
            if (x >= 1) return 1;
            // Bisection on the x curve: monotonic because x1, x2 are within [0, 1].
            double lo = 0, hi = 1, t = x;
            for (var k = 0; k < 40; k++)
            {
                t = (lo + hi) / 2;
                var cx = Coord(t, x1, x2);
                if (Math.Abs(cx - x) < 1e-7) break;
                if (cx < x) lo = t; else hi = t;
            }
            return Coord(t, y1, y2);
        });
    }
}

/// <summary>Reading and blending the values animations move between.</summary>
public static partial class ArtValues
{
    [GeneratedRegex(@"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?")]
    private static partial Regex NumberRegex();

    /// <summary>A clock value (<c>2s</c>, <c>500ms</c>, <c>1.5</c>, <c>0:01.5</c>) in seconds, or null.</summary>
    public static double? Seconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim().ToLowerInvariant();
        double Num(string s) => double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
        double r;
        if (v.EndsWith("ms")) r = Num(v[..^2]) / 1000;
        else if (v.EndsWith("min")) r = Num(v[..^3]) * 60;
        else if (v.EndsWith('s')) r = Num(v[..^1]);
        else if (v.EndsWith('h')) r = Num(v[..^1]) * 3600;
        else if (v.Contains(':'))
        {
            var parts = v.Split(':');
            r = 0;
            foreach (var p in parts) r = r * 60 + Num(p);
        }
        else r = Num(v);
        return double.IsFinite(r) ? r : null;
    }

    /// <summary>A plain number (units after it ignored), or null.</summary>
    public static double? Number(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var m = NumberRegex().Match(value);
        if (!m.Success) return null;
        var v = double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
        return value.TrimEnd().EndsWith('%') ? v / 100 : v;
    }

    /// <summary>
    /// A value between two others: colours channel by channel, text with the same shape number by
    /// number (lengths, paths with matching commands, point lists), anything else switching halfway.
    /// </summary>
    public static string Blend(string from, string to, double p)
    {
        if (p <= 0) return from;
        if (p >= 1) return to;
        var ca = SvgSanitizer.NormalizeColor(from);
        var cb = SvgSanitizer.NormalizeColor(to);
        if (ca is not null && cb is not null)
        {
            var a = Convert.FromHexString(ca[1..]);
            var b = Convert.FromHexString(cb[1..]);
            return "#" + string.Concat(Enumerable.Range(0, 3).Select(i =>
                ((int)Math.Round(a[i] + (b[i] - a[i]) * p)).ToString("x2", CultureInfo.InvariantCulture)));
        }
        var na = NumberRegex().Matches(from);
        var nb = NumberRegex().Matches(to);
        if (na.Count > 0 && na.Count == nb.Count && NumberRegex().Replace(from, "#") == NumberRegex().Replace(to, "#"))
        {
            var k = 0;
            return NumberRegex().Replace(from, m =>
            {
                var x = double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                var y = double.Parse(nb[k++].Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                return Format(x + (y - x) * p);
            });
        }
        return p < 0.5 ? from : to;
    }

    public static string Format(double v)
    {
        var s = Math.Round(v, 4).ToString("0.####", CultureInfo.InvariantCulture);
        return s == "-0" ? "0" : s;
    }

    /// <summary>
    /// Rounds every number in a geometry attribute to <paramref name="decimals"/> places — most of an
    /// exported illustration's weight is digits nobody can see. A space goes in where rounding would
    /// glue two numbers together (<c>1.5.5</c> is two numbers in path syntax).
    /// </summary>
    public static string Compact(string value, int decimals)
    {
        var sb = new StringBuilder(value.Length);
        var last = 0;
        foreach (Match m in NumberRegex().Matches(value))
        {
            sb.Append(value, last, m.Index - last);
            var d = double.Parse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
            var text = Math.Round(d, decimals).ToString(decimals == 0 ? "0" : "0." + new string('#', decimals), CultureInfo.InvariantCulture);
            if (text == "-0") text = "0";
            if (sb.Length > 0 && (char.IsAsciiDigit(sb[^1]) || sb[^1] == '.') && !text.StartsWith('-')) sb.Append(' ');
            sb.Append(text);
            last = m.Index + m.Length;
        }
        sb.Append(value, last, value.Length - last);
        return sb.ToString();
    }
}
