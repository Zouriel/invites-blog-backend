using InvitesBlog.Application.Campaigns;
using InvitesBlog.Domain.Entities;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>The colours a picture-less tile falls back to. See <see cref="PosterColours"/>.</summary>
public class PosterColoursTests
{
    [Fact]
    public void Background_then_accent_from_the_template_with_the_hosts_choice_on_top()
    {
        var c = new Campaign
        {
            TemplateManifestJson = """{"theme":{"accentColor":"#C9A227","backgroundColor":"#fdf1f4","textColor":"#222222"}}""",
            ThemeOverridesJson = """{"shared":{"accentColor":"#123456"}}""",
        };
        Assert.Equal(["#fdf1f4", "#123456"], PosterColours.Of(c));
    }

    [Fact]
    public void Anything_but_six_digit_hex_is_left_out_because_it_lands_in_css()
    {
        var c = new Campaign
        {
            TemplateManifestJson = """{"theme":{"accentColor":"red;background:url(x)","backgroundColor":"#FFF"}}""",
        };
        Assert.Null(PosterColours.Of(c));
    }

    [Fact]
    public void No_declared_theme_is_null_so_the_client_uses_its_own() =>
        Assert.Null(PosterColours.Of(new Campaign()));
}
