using System.Text.Json.Nodes;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.Application.Dtos.Campaigns;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>The rules behind the Colours page. See <see cref="GuestColours"/>.</summary>
public class GuestColoursTests
{
    private static readonly ColourSetDto Pink = new("#D4627F", "#fdf1f4", "#4a2a33");

    /// <summary>Values land in CSS, so anything but #rrggbb is refused rather than cleaned.</summary>
    [Theory]
    [InlineData("#c9a227", true)]
    [InlineData("#C9A227", true)]
    [InlineData("#fff", false)]
    [InlineData("red", false)]
    [InlineData("#c9a22", false)]
    [InlineData("#c9a227;}body{display:none", false)]
    [InlineData("#c9a227 ", false)]
    [InlineData(null, false)]
    public void Only_six_digit_hex_is_a_colour(string? value, bool ok) => Assert.Equal(ok, GuestColours.IsHex(value));

    [Fact]
    public void A_set_with_one_bad_colour_is_invalid() =>
        Assert.False(GuestColours.IsValid(Pink with { Text = "url(x)" }));

    [Fact]
    public void Reply_colours_go_into_the_flat_theme_and_leave_other_keys_alone()
    {
        var json = GuestColours.WriteReply("""{"fontHeading":"Lora","accentColor":"#000000"}""", Pink);
        var o = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("Lora", o["fontHeading"]!.ToString());
        Assert.Equal("#d4627f", o["accentColor"]!.ToString());   // stored lower-case
        Assert.Equal(Pink with { Accent = "#d4627f" }, GuestColours.ReadReply(json));
    }

    /// <summary>The newer shape keeps per-role overrides; the reply colours belong to everyone, so they go in shared.</summary>
    [Fact]
    public void Reply_colours_go_into_shared_when_the_theme_has_roles()
    {
        var json = GuestColours.WriteReply("""{"roles":{"Bride":{"accentColor":"#111111"}}}""", Pink);
        var o = JsonNode.Parse(json)!.AsObject();

        Assert.Equal("#d4627f", o["shared"]!["accentColor"]!.ToString());
        Assert.Equal("#111111", o["roles"]!["Bride"]!["accentColor"]!.ToString());
    }

    [Fact]
    public void Resetting_reply_colours_removes_only_those_keys()
    {
        var json = GuestColours.WriteReply(GuestColours.WriteReply("""{"fontHeading":"Lora"}""", Pink), null);
        var o = JsonNode.Parse(json)!.AsObject();

        Assert.Null(o["accentColor"]);
        Assert.Null(o["backgroundColor"]);
        Assert.Equal("Lora", o["fontHeading"]!.ToString());
        Assert.Null(GuestColours.ReadReply(json));
    }

    [Fact]
    public void Card_colours_round_trip_and_reset_to_empty()
    {
        Assert.Equal(Pink with { Accent = "#d4627f" }, GuestColours.ReadCard(GuestColours.WriteCard(Pink)));
        Assert.Equal("{}", GuestColours.WriteCard(null));
        Assert.Null(GuestColours.ReadCard("{}"));
        Assert.Null(GuestColours.ReadCard("not json"));
    }

    /// <summary>
    /// A pink design (blush edge, rose artwork, a little dark text): the suggestion is pink, its button
    /// shows against its background, and its words are readable.
    /// </summary>
    [Fact]
    public void A_pink_design_suggests_a_readable_pink_palette()
    {
        var read = new ImageColours("#f9e4ea", [("#f9e4ea", 0.6), ("#d4627f", 0.25), ("#3a2a2e", 0.05), ("#ffffff", 0.1)]);

        var suggestions = GuestColours.Suggest(read);

        Assert.NotEmpty(suggestions);
        foreach (var s in suggestions)
        {
            var c = s.Colours;
            Assert.True(GuestColours.IsValid(c));
            Assert.True(GuestColours.Ratio(c.Text, c.Background) >= 4.5, $"{s.Name}: text {c.Text} on {c.Background}");
            Assert.True(GuestColours.Ratio(c.Accent, c.Background) >= 2.2, $"{s.Name}: button {c.Accent} on {c.Background}");
        }
        var first = suggestions[0].Colours;
        Assert.Equal("#f9e4ea", first.Background);
        // Still recognisably the rose from the picture: red well above blue and green.
        var r = Convert.ToInt32(first.Accent[1..3], 16); var g = Convert.ToInt32(first.Accent[3..5], 16);
        Assert.True(r > g + 40, $"accent {first.Accent} is not pink");
    }

    /// <summary>
    /// The case found on a real upload: a soft pink gradient fills most of the picture and the vivid
    /// rose is only in the lettering. The button should be that rose, not the dusty mid-tone of the
    /// gradient, which is what a pick weighted by area chose.
    /// </summary>
    [Fact]
    public void The_vivid_lettering_beats_a_large_dull_gradient()
    {
        var read = new ImageColours("#f6ccd8",
            [("#fbe3ea", 0.35), ("#f6ccd8", 0.3), ("#f3b6c8", 0.2), ("#ab8691", 0.1), ("#b0405f", 0.015), ("#5a2a38", 0.01)]);

        var accent = GuestColours.Suggest(read)[0].Colours.Accent;

        var r = Convert.ToInt32(accent[1..3], 16); var g = Convert.ToInt32(accent[3..5], 16);
        Assert.True(r - g > 90, $"accent {accent} is not the rose");
    }

    [Fact]
    public void A_picture_of_one_colour_still_gets_a_visible_button()
    {
        var s = GuestColours.Suggest(new ImageColours("#ffffff", [("#ffffff", 1.0)]));
        var c = Assert.Single(s.Take(1)).Colours;
        Assert.True(GuestColours.Ratio(c.Accent, c.Background) >= 2.2);
    }

    [Fact]
    public void Nothing_read_means_nothing_suggested() => Assert.Empty(GuestColours.Suggest(null));
}
