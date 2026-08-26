using InvitesBlog.Api.Rendering;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// The colours the plain guest pages derive from an invitation's three authored ones.
///
/// <para>These pages used to be one fixed dark-gold set, so a guest on a pale invitation tapped RSVP
/// and landed somewhere that looked like a different product. What matters now is that a derived
/// palette stays legible in both directions — light text does not end up on a light card.</para>
/// </summary>
public class GuestPaletteTests
{
    [Fact]
    public void Nothing_authored_keeps_the_original_palette()
    {
        Assert.Equal(GuestPalette.Fallback, GuestPalette.From(null, null, null));
    }

    [Fact]
    public void A_pale_background_produces_a_light_scheme_with_dark_text()
    {
        var p = GuestPalette.From("#8a6d3b", "#fdf8f0", null);

        Assert.False(p.Dark);
        Assert.Equal("light", p.Scheme);
        // Text was not authored, so it is chosen against the background rather than left light.
        Assert.True(Luminance(p.Ink) < 0.5, $"ink {p.Ink} should be dark on a pale ground");
    }

    [Fact]
    public void A_dark_background_produces_a_dark_scheme_with_light_text()
    {
        var p = GuestPalette.From("#c9a227", "#101014", null);

        Assert.True(p.Dark);
        Assert.True(Luminance(p.Ink) > 0.5, $"ink {p.Ink} should be light on a dark ground");
    }

    /// <summary>The card and the rules must separate from the background without swamping it.</summary>
    [Fact]
    public void Surfaces_sit_between_the_background_and_the_text()
    {
        var p = GuestPalette.From("#c9a227", "#ffffff", "#000000");

        Assert.NotEqual(p.Bg, p.Card);
        Assert.True(Luminance(p.Card) < Luminance(p.Bg), "card should step toward the text");
        Assert.True(Luminance(p.Line) < Luminance(p.Card), "a rule is a bigger step than a card");
        // Muted text stays readable — nearer the text than the background.
        Assert.True(Luminance(p.Muted) < 0.5);
    }

    [Fact]
    public void A_button_label_is_chosen_against_its_accent()
    {
        Assert.Equal("#1a1408", GuestPalette.From("#ffe680", "#ffffff", null).OnAccent);
        Assert.Equal("#ffffff", GuestPalette.From("#4a1d96", "#ffffff", null).OnAccent);
    }

    /// <summary>
    /// A template may write a colour in a form CSS understands and this does not. A guest standing at
    /// a party still gets a page.
    /// </summary>
    [Theory]
    [InlineData("rgb(201, 162, 39)")]
    [InlineData("var(--brand)")]
    [InlineData("linear-gradient(#fff, #000)")]
    [InlineData("goldenrod")]
    [InlineData("#12345")]
    [InlineData("")]
    public void An_unreadable_colour_falls_back_rather_than_failing(string value)
    {
        var p = GuestPalette.From(value, value, value);
        Assert.Equal(GuestPalette.Fallback, p);
    }

    [Fact]
    public void Short_hex_is_accepted()
    {
        Assert.Equal(GuestPalette.From("#c9a227", "#fff", null), GuestPalette.From("#c9a227", "#ffffff", null));
    }

    /// <summary>Every page opens with this, so it must actually carry the derived values.</summary>
    [Fact]
    public void The_root_block_declares_the_derived_values()
    {
        var p = GuestPalette.From("#c9a227", "#fdf8f0", "#221a10");

        Assert.Contains("color-scheme: light", p.Root);
        Assert.Contains("--accent:#c9a227", p.Root);
        Assert.Contains($"--bg:{p.Bg}", p.Root);
        Assert.Contains($"--on-accent:{p.OnAccent}", p.Root);
    }

    private static double Luminance(string hex)
    {
        var v = hex.TrimStart('#');
        var r = Convert.ToInt32(v[..2], 16);
        var g = Convert.ToInt32(v.Substring(2, 2), 16);
        var b = Convert.ToInt32(v.Substring(4, 2), 16);
        return (r * 0.299 + g * 0.587 + b * 0.114) / 255.0;
    }
}
