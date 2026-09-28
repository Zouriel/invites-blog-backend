using System.Globalization;
using System.Text;

namespace InvitesBlog.TemplateCompiler.Design.Art;

/// <summary>
/// Rewrites SVG path data as short as it can be at a given precision: relative commands, a command
/// letter only when it changes, no leading zeros, no separator a sign or a second dot already implies.
/// Illustration exports write absolute coordinates to six places; this is usually a third to a half of
/// an SVG's weight gone with nothing visible changed.
///
/// <para>Relative steps are taken from the <b>rounded</b> position already written, not the exact one,
/// so rounding can't drift along a long path. Anything unreadable comes back unchanged.</para>
/// </summary>
public static class PathData
{
    public static string Compact(string d, int decimals)
    {
        var tokens = PathGeometry.Tokenize(d);
        var sb = new StringBuilder(d.Length / 2);
        var format = decimals == 0 ? "0" : "0." + new string('#', decimals);
        char? written = null;
        double cx = 0, cy = 0, sx = 0, sy = 0; // exact current point and subpath start
        double rx = 0, ry = 0, rsx = 0, rsy = 0; // the same, as written (rounded)
        var i = 0;
        char cmd = ' ';

        double R(double v) => Math.Round(v, decimals);
        void Letter(char c)
        {
            // After a moveto, further pairs are linetos: no letter needed for those.
            var implied = written == 'm' && c == 'l' ? 'l' : written;
            if (implied != c || c == 'm') sb.Append(c);
            written = c;
        }
        void Num(double v)
        {
            var text = v.ToString(format, CultureInfo.InvariantCulture);
            if (text == "-0") text = "0";
            if (text.StartsWith("0.", StringComparison.Ordinal)) text = text[1..];
            else if (text.StartsWith("-0.", StringComparison.Ordinal)) text = "-" + text[2..];
            if (sb.Length > 0)
            {
                var last = sb[^1];
                var needs = char.IsAsciiDigit(last) || last == '.';
                if (needs && text[0] == '-') needs = false;
                else if (needs && text[0] == '.' && !EndsInWholeNumber(sb)) needs = false;
                if (needs) sb.Append(' ');
            }
            sb.Append(text);
        }
        bool Next(out double v)
        {
            if (i < tokens.Count && tokens[i].IsNumber) { v = tokens[i++].Value; return double.IsFinite(v); }
            v = 0;
            return false;
        }

        var guard = 0;
        while (i < tokens.Count && guard++ < 1_000_000)
        {
            if (!tokens[i].IsNumber) cmd = tokens[i++].Command;
            else if (cmd == 'M') cmd = 'L';
            else if (cmd == 'm') cmd = 'l';
            else if (cmd is ' ' or 'Z' or 'z') return d;

            var rel = char.IsLower(cmd);
            double ox = rel ? cx : 0, oy = rel ? cy : 0;
            switch (char.ToUpperInvariant(cmd))
            {
                case 'M':
                {
                    if (!Next(out var x) || !Next(out var y)) return d;
                    cx = sx = x + ox; cy = sy = y + oy;
                    Letter('m');
                    var (dx, dy) = (R(cx - rx), R(cy - ry));
                    Num(dx); Num(dy);
                    rx = rsx = rx + dx; ry = rsy = ry + dy;
                    break;
                }
                case 'L':
                {
                    if (!Next(out var x) || !Next(out var y)) return d;
                    cx = x + ox; cy = y + oy;
                    var (dx, dy) = (R(cx - rx), R(cy - ry));
                    if (dy == 0 && dx != 0) { Letter('h'); Num(dx); }
                    else if (dx == 0 && dy != 0) { Letter('v'); Num(dy); }
                    else { Letter('l'); Num(dx); Num(dy); }
                    rx += dx; ry += dy;
                    break;
                }
                case 'H':
                {
                    if (!Next(out var x)) return d;
                    cx = x + ox;
                    var dx = R(cx - rx);
                    Letter('h'); Num(dx);
                    rx += dx;
                    break;
                }
                case 'V':
                {
                    if (!Next(out var y)) return d;
                    cy = y + (rel ? cy : 0);
                    var dy = R(cy - ry);
                    Letter('v'); Num(dy);
                    ry += dy;
                    break;
                }
                case 'C':
                case 'S':
                case 'Q':
                case 'T':
                {
                    var up = char.ToUpperInvariant(cmd);
                    var pairs = up switch { 'C' => 3, 'S' or 'Q' => 2, _ => 1 };
                    var pts = new (double X, double Y)[pairs];
                    for (var k = 0; k < pairs; k++)
                    {
                        if (!Next(out var x) || !Next(out var y)) return d;
                        pts[k] = (x + ox, y + oy);
                    }
                    Letter(char.ToLowerInvariant(up));
                    foreach (var (x, y) in pts) { Num(R(x - rx)); Num(R(y - ry)); }
                    var (ex, ey) = pts[^1];
                    var (dx, dy) = (R(ex - rx), R(ey - ry));
                    cx = ex; cy = ey; rx += dx; ry += dy;
                    break;
                }
                case 'A':
                {
                    if (!Next(out var arx) || !Next(out var ary) || !Next(out var rot) || !Next(out var large) || !Next(out var sweep)
                        || !Next(out var x) || !Next(out var y)) return d;
                    cx = x + ox; cy = y + oy;
                    Letter('a');
                    Num(R(arx)); Num(R(ary)); Num(R(rot)); Num(large != 0 ? 1 : 0); Num(sweep != 0 ? 1 : 0);
                    var (dx, dy) = (R(cx - rx), R(cy - ry));
                    Num(dx); Num(dy);
                    rx += dx; ry += dy;
                    break;
                }
                case 'Z':
                    Letter('z');
                    written = 'z';
                    cx = sx; cy = sy; rx = rsx; ry = rsy;
                    cmd = ' ';
                    break;
                default:
                    return d;
            }
        }
        return sb.ToString();
    }

    /// <summary>True when the number just written has no decimal point, so a following ".5" needs a space.</summary>
    private static bool EndsInWholeNumber(StringBuilder sb)
    {
        for (var k = sb.Length - 1; k >= 0; k--)
        {
            var c = sb[k];
            if (c == '.') return false;
            if (!char.IsAsciiDigit(c)) return true;
        }
        return true;
    }
}
