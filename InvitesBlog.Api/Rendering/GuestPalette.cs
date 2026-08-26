using System.Globalization;

namespace InvitesBlog.Api.Rendering;

/// <summary>
/// The colours the plain guest pages paint themselves in — derived from the invitation the guest just
/// came from, rather than fixed.
///
/// <para><b>Why.</b> These pages used to hard-code one dark-gold palette. That matched the templates
/// that happened to be dark and gold and nothing else, so a guest tapping RSVP on a pale invitation
/// landed on what looked like a different website. Only three colours are actually authored — accent,
/// background, text — so the rest are derived from them here: a card is a small step from the
/// background toward the text, a rule is a larger one, muted text is a step back toward the
/// background. That keeps contrast sane on a light palette and a dark one without asking a designer
/// to specify seven values.</para>
///
/// <para>Anything unparseable falls back rather than failing. A template may express a colour in a
/// form CSS understands and this does not (<c>rgb()</c>, a gradient, another custom property), and a
/// guest standing at a party should still get a page.</para>
/// </summary>
public sealed record GuestPalette(
    string Bg, string Card, string Ink, string Muted, string Accent, string Line, string Bad,
    string OnAccent, bool Dark)
{
    /// <summary>What every one of these pages looked like before they could be themed.</summary>
    public static readonly GuestPalette Fallback = new(
        Bg: "#17131a", Card: "#211b25", Ink: "#f4eef6", Muted: "#b9adbf", Accent: "#c9a227",
        Line: "#372e3d", Bad: "#ff8f8f", OnAccent: "#241d06", Dark: true);

    public string Scheme => Dark ? "dark" : "light";

    /// <summary>The <c>:root</c> block every guest page opens with.</summary>
    public string Root => $$"""
        :root { color-scheme: {{Scheme}}; --bg:{{Bg}}; --card:{{Card}}; --ink:{{Ink}}; --muted:{{Muted}};
                --accent:{{Accent}}; --line:{{Line}}; --bad:{{Bad}}; --on-accent:{{OnAccent}}; }
        """;

    public static GuestPalette From(string? accent, string? background, string? text)
    {
        var bg = Parse(background);
        var ink = Parse(text);
        var brand = Parse(accent);

        // Nothing usable authored: leave it exactly as it was.
        if (bg is null && ink is null && brand is null) return Fallback;

        // A background we cannot read is the one that decides light-vs-dark, so without it we stay on
        // the known-good dark set and only borrow the accent.
        if (bg is null)
        {
            return Fallback with
            {
                Accent = brand is null ? Fallback.Accent : accent!,
                OnAccent = brand is null ? Fallback.OnAccent : Contrast(brand.Value),
                Ink = ink is null ? Fallback.Ink : text!,
            };
        }

        var background_ = bg.Value;
        var foreground = ink ?? (IsDark(background_) ? Rgb(0xf4, 0xee, 0xf6) : Rgb(0x17, 0x13, 0x1a));
        var dark = IsDark(background_);

        return new GuestPalette(
            Bg: Hex(background_),
            Card: Hex(Mix(background_, foreground, 0.07)),
            Ink: Hex(foreground),
            Muted: Hex(Mix(foreground, background_, 0.38)),
            Accent: brand is null ? Fallback.Accent : accent!,
            Line: Hex(Mix(background_, foreground, 0.18)),
            // Red on a dark ground has to be lifted to stay legible; on a light one it has to be sunk.
            Bad: dark ? "#ff8f8f" : "#b3261e",
            OnAccent: Contrast(brand ?? Rgb(0xc9, 0xa2, 0x27)),
            Dark: dark);
    }

    // ----- colour arithmetic -----

    private readonly record struct Colour(int R, int G, int B);

    private static Colour Rgb(int r, int g, int b) => new(r, g, b);

    /// <summary>Accepts <c>#rgb</c> and <c>#rrggbb</c>; anything else is not ours to interpret.</summary>
    private static Colour? Parse(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v) || v[0] != '#') return null;
        v = v[1..];

        if (v.Length == 3)
            v = string.Concat(v.Select(c => new string(c, 2)));
        if (v.Length != 6) return null;

        return int.TryParse(v, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var packed)
            ? new Colour((packed >> 16) & 0xff, (packed >> 8) & 0xff, packed & 0xff)
            : null;
    }

    private static string Hex(Colour c) => $"#{c.R:x2}{c.G:x2}{c.B:x2}";

    /// <summary>Moves <paramref name="from"/> a fraction of the way toward <paramref name="to"/>.</summary>
    private static Colour Mix(Colour from, Colour to, double amount) => new(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    /// <summary>Perceived brightness (ITU-R BT.601), which tracks the eye better than a plain average.</summary>
    private static double Luminance(Colour c) => (c.R * 0.299 + c.G * 0.587 + c.B * 0.114) / 255.0;

    private static bool IsDark(Colour c) => Luminance(c) < 0.5;

    /// <summary>Text that stays readable ON a colour — the label of a filled button, mostly.</summary>
    private static string Contrast(Colour c) => Luminance(c) > 0.55 ? "#1a1408" : "#ffffff";
}
