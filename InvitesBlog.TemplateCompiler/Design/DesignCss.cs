using System.Globalization;
using System.Text.RegularExpressions;

namespace InvitesBlog.TemplateCompiler.Design;

/// <summary>
/// Turns scene values into CSS text. Every value a customer controls passes through here and comes
/// out either as something this class built from validated parts, or not at all — there is no path
/// where a string from the scene is pasted into a stylesheet. That is what lets a designed template
/// go live without a human reading it.
/// </summary>
public static partial class DesignCss
{
    [GeneratedRegex("^#(?:[0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexRegex();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,31}$")]
    private static partial Regex ThemeKeyRegex();

    [GeneratedRegex(@"^cubic-bezier\(\s*(-?\d*\.?\d+)\s*,\s*(-?\d*\.?\d+)\s*,\s*(-?\d*\.?\d+)\s*,\s*(-?\d*\.?\d+)\s*\)$")]
    private static partial Regex BezierRegex();

    public static bool IsHex(string? value) => value is not null && HexRegex().IsMatch(value);

    public static bool IsThemeKey(string? key) => key is not null && ThemeKeyRegex().IsMatch(key);

    /// <summary>A number as CSS wants it: invariant culture, at most three decimals, never NaN.</summary>
    public static string Num(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value)) value = 0;
        var rounded = Math.Round(value, 3);
        if (rounded == 0) return "0"; // no "-0"
        return rounded.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>A length in canvas units: <c>calc(N * var(--u))</c>.</summary>
    public static string U(double units) => units == 0 ? "0px" : $"calc({Num(units)} * var(--u))";

    /// <summary>
    /// A colour reference — <c>theme:{key}</c> or a hex colour — as CSS. Null when the reference is
    /// empty, malformed, or names a theme key the scene doesn't declare.
    /// </summary>
    public static string? Color(string? reference, ISet<string> themeKeys)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (reference == "none" || reference == "transparent") return "transparent";
        if (reference.StartsWith("theme:", StringComparison.Ordinal))
        {
            var key = reference[6..];
            return IsThemeKey(key) && themeKeys.Contains(key) ? $"var(--ib-{key})" : null;
        }
        return IsHex(reference) ? reference.ToLowerInvariant() : null;
    }

    /// <summary>A font reference — <c>theme:{key}</c> or a catalog font id — as a <c>font-family</c> value.</summary>
    public static string? Font(string? reference, ISet<string> themeKeys)
    {
        if (string.IsNullOrWhiteSpace(reference)) return null;
        if (reference.StartsWith("theme:", StringComparison.Ordinal))
        {
            var key = reference[6..];
            return IsThemeKey(key) && themeKeys.Contains(key) ? $"var(--ib-{key})" : null;
        }
        return DesignCatalog.FindFont(reference)?.Stack;
    }

    /// <summary>An easing keyword or a well-formed <c>cubic-bezier()</c>, else null.</summary>
    public static string? Easing(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var v = value.Trim();
        if (DesignCatalog.Easings.Contains(v)) return v;
        var m = BezierRegex().Match(v);
        if (!m.Success) return null;
        var p = Enumerable.Range(1, 4)
            .Select(i => double.Parse(m.Groups[i].Value, CultureInfo.InvariantCulture))
            .ToArray();
        // x coordinates of a timing function must stay within [0, 1] or the function is invalid.
        if (p[0] is < 0 or > 1 || p[2] is < 0 or > 1) return null;
        return $"cubic-bezier({Num(p[0])},{Num(p[1])},{Num(p[2])},{Num(p[3])})";
    }

    public static double Clamp(double value, double min, double max) =>
        double.IsNaN(value) ? min : Math.Min(max, Math.Max(min, value));
}
