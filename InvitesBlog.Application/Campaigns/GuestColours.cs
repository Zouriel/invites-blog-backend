using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InvitesBlog.Application.Dtos.Campaigns;

namespace InvitesBlog.Application.Campaigns;

/// <summary>
/// The colours a host picks for what the platform adds around an uploaded design (the reply button,
/// the guest pages) and for the printed QR table card.
///
/// <para><b>Reply colours live in <c>ThemeOverridesJson</c></b>, as the same three keys a template's
/// Theme step writes (<c>accentColor</c>, <c>backgroundColor</c>, <c>textColor</c>). That is what every
/// existing path already reads: the renderer turns them into <c>--ib-*</c> on the invitation, which the
/// appended bars and an uploaded design's page use, and the guest pages build their palette from them.
/// Only ever written for an uploaded design: on a template the same keys recolour the template itself,
/// and those are the host's Theme-step choices.</para>
///
/// <para><b>Only <c>#rrggbb</c> is accepted.</b> These values end up inside CSS (an inline style and a
/// <c>&lt;style&gt;</c> block), so anything looser is a way to write CSS of one's own.</para>
/// </summary>
public static partial class GuestColours
{
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex HexPattern();

    public static bool IsHex(string? value) => value is not null && HexPattern().IsMatch(value);

    public static bool IsValid(ColourSetDto? set) =>
        set is null || (IsHex(set.Accent) && IsHex(set.Background) && IsHex(set.Text));

    private static ColourSetDto Normalise(ColourSetDto s) =>
        new(s.Accent.ToLowerInvariant(), s.Background.ToLowerInvariant(), s.Text.ToLowerInvariant());

    // ----- reply colours, inside ThemeOverridesJson -----

    /// <summary>The shared layer of a theme blob: <c>shared</c> in the newer shape, the whole object in the old flat one.</summary>
    private static JsonObject SharedLayer(JsonObject theme) =>
        theme["shared"] is JsonObject shared || theme["roles"] is JsonObject
            ? theme["shared"] as JsonObject ?? (JsonObject)(theme["shared"] = new JsonObject())
            : theme;

    public static ColourSetDto? ReadReply(string? themeJson)
    {
        var layer = SharedLayer(Parse(themeJson));
        var a = layer["accentColor"]?.ToString();
        var b = layer["backgroundColor"]?.ToString();
        var t = layer["textColor"]?.ToString();
        return IsHex(a) && IsHex(b) && IsHex(t) ? new ColourSetDto(a!, b!, t!) : null;
    }

    /// <summary>
    /// Sets (or, given null, removes) the three keys and leaves everything else in the blob alone,
    /// in whichever shape it already has.
    /// </summary>
    public static string WriteReply(string? themeJson, ColourSetDto? set)
    {
        var theme = Parse(themeJson);
        var layer = SharedLayer(theme);
        if (set is null)
        {
            layer.Remove("accentColor");
            layer.Remove("backgroundColor");
            layer.Remove("textColor");
        }
        else
        {
            var s = Normalise(set);
            layer["accentColor"] = s.Accent;
            layer["backgroundColor"] = s.Background;
            layer["textColor"] = s.Text;
        }
        return theme.ToJsonString();
    }

    // ----- the table card, in CardThemeJson -----

    public static ColourSetDto? ReadCard(string? cardJson)
    {
        var o = Parse(cardJson);
        var a = o["accent"]?.ToString();
        var b = o["background"]?.ToString();
        var t = o["text"]?.ToString();
        return IsHex(a) && IsHex(b) && IsHex(t) ? new ColourSetDto(a!, b!, t!) : null;
    }

    public static string WriteCard(ColourSetDto? set)
    {
        if (set is null) return "{}";
        var s = Normalise(set);
        return new JsonObject { ["accent"] = s.Accent, ["background"] = s.Background, ["text"] = s.Text }.ToJsonString();
    }

    private static JsonObject Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (System.Text.Json.JsonException) { return new JsonObject(); }
    }

    // ----- suggestions from the design itself -----

    /// <summary>
    /// Up to two colour sets read off the uploaded picture: one on the colour around its edge (so the
    /// bars run on from the artwork), one on a pale wash of its strongest colour. Text is always pushed
    /// to readable contrast; a suggestion that can't find a distinct accent uses a deeper shade of the
    /// background instead.
    /// </summary>
    public static IReadOnlyList<SuggestedColoursDto> Suggest(ImageColours? read)
    {
        if (read is null || read.Dominant.Count == 0) return [];

        var edge = FromHex(read.Edge);
        // The vivid colour first: lettering covers little of a card, and a pick by area alone found the
        // gradient around it instead.
        var accent = read.Vivid is { } vivid && IsHex(vivid)
            ? FromHex(vivid)
            : PickAccent(read.Dominant.Select(d => (FromHex(d.Hex), d.Share)).ToList(), edge);

        var matched = Set(edge, accent);
        var paleBg = Mix(accent, (255, 255, 255), 0.9);
        var pale = Set(paleBg, accent);

        var list = new List<SuggestedColoursDto> { new("Matches your design", matched) };
        if (!SameSet(matched, pale)) list.Add(new SuggestedColoursDto("Light, from your design", pale));
        return list;
    }

    private static ColourSetDto Set((int R, int G, int B) bg, (int R, int G, int B) accent)
    {
        // A button that disappears into its background is no button: deepen or lift it until it shows.
        var a = accent;
        for (var i = 0; i < 6 && Ratio(a, bg) < 2.2; i++)
            a = Luminance(bg) > 0.5 ? Mix(a, (0, 0, 0), 0.2) : Mix(a, (255, 255, 255), 0.2);

        // Body text: a deep (or pale) version of the accent, so it belongs to the palette, then pushed
        // until it reads at the 4.5:1 that small text needs.
        var light = Luminance(bg) > 0.5;
        var text = light ? Mix(a, (0, 0, 0), 0.65) : Mix(a, (255, 255, 255), 0.85);
        for (var i = 0; i < 8 && Ratio(text, bg) < 4.5; i++)
            text = light ? Mix(text, (0, 0, 0), 0.35) : Mix(text, (255, 255, 255), 0.35);

        return new ColourSetDto(Hex(a), Hex(bg), Hex(text));
    }

    /// <summary>The most colourful of the common colours that stands apart from the background.</summary>
    private static (int R, int G, int B) PickAccent(List<((int R, int G, int B) C, double Share)> colours, (int R, int G, int B) bg)
    {
        var best = colours
            .Where(c => Distance(c.C, bg) > 60)
            .OrderByDescending(c => Saturation(c.C) * Math.Sqrt(c.Share))
            .Select(c => ((int R, int G, int B)?)c.C)
            .FirstOrDefault();
        return best ?? Deepen(bg);
    }

    /// <summary>
    /// A stronger colour of the same hue: on a pale ground a deeper, richer one; on a dark ground a
    /// lighter one. Mixing toward black or white instead only greys it (pale pink turned mauve-grey).
    /// </summary>
    private static (int R, int G, int B) Deepen((int R, int G, int B) c)
    {
        var (h, s, l) = ToHsl(c);
        var light = Luminance(c) > 0.5;
        // A colour with almost no hue (white, grey) has none to deepen; it gets a neutral deep tone.
        var sat = s < 0.08 ? 0 : Math.Max(s, 0.55);
        return FromHsl(h, sat, light ? 0.42 : 0.72);
    }

    private static (double H, double S, double L) ToHsl((int R, int G, int B) c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        var l = (max + min) / 2;
        if (max == min) return (0, 0, l);
        var d = max - min;
        var s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
        var h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
        return (h / 6, s, l);
    }

    private static (int R, int G, int B) FromHsl(double h, double s, double l)
    {
        if (s == 0) { var v = (int)Math.Round(l * 255); return (v, v, v); }
        static double Hue(double p, double q, double t)
        {
            if (t < 0) t += 1; if (t > 1) t -= 1;
            if (t < 1.0 / 6) return p + (q - p) * 6 * t;
            if (t < 1.0 / 2) return q;
            if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
            return p;
        }
        var q = l < 0.5 ? l * (1 + s) : l + s - l * s;
        var p = 2 * l - q;
        return ((int)Math.Round(Hue(p, q, h + 1.0 / 3) * 255), (int)Math.Round(Hue(p, q, h) * 255), (int)Math.Round(Hue(p, q, h - 1.0 / 3) * 255));
    }

    private static bool SameSet(ColourSetDto a, ColourSetDto b) =>
        a.Accent == b.Accent && a.Background == b.Background && a.Text == b.Text;

    private static (int R, int G, int B) FromHex(string hex)
    {
        var v = int.Parse(hex.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return ((v >> 16) & 0xff, (v >> 8) & 0xff, v & 0xff);
    }

    private static string Hex((int R, int G, int B) c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    private static (int R, int G, int B) Mix((int R, int G, int B) a, (int R, int G, int B) b, double t) =>
        ((int)Math.Round(a.R + (b.R - a.R) * t), (int)Math.Round(a.G + (b.G - a.G) * t), (int)Math.Round(a.B + (b.B - a.B) * t));

    private static double Distance((int R, int G, int B) a, (int R, int G, int B) b) =>
        Math.Sqrt(Math.Pow(a.R - b.R, 2) + Math.Pow(a.G - b.G, 2) + Math.Pow(a.B - b.B, 2));

    private static double Saturation((int R, int G, int B) c)
    {
        var max = Math.Max(c.R, Math.Max(c.G, c.B)) / 255.0;
        var min = Math.Min(c.R, Math.Min(c.G, c.B)) / 255.0;
        return max == 0 ? 0 : (max - min) / max;
    }

    /// <summary>WCAG relative luminance.</summary>
    private static double Luminance((int R, int G, int B) c)
    {
        static double Channel(int v) { var s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
    }

    /// <summary>WCAG contrast ratio, 1 to 21.</summary>
    public static double Ratio((int R, int G, int B) a, (int R, int G, int B) b)
    {
        var la = Luminance(a); var lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public static double Ratio(string hexA, string hexB) => Ratio(FromHex(hexA), FromHex(hexB));
}

/// <summary>
/// What an image's pixels say about its colours: the average round its edge, its most common colours
/// with their share (0–1), and its vivid colour (the average of the strongly coloured pixels that stand
/// apart from the edge, which is usually the lettering), or null when it has none.
/// </summary>
public sealed record ImageColours(string Edge, IReadOnlyList<(string Hex, double Share)> Dominant, string? Vivid = null);
