using InvitesBlog.Application.Services.Portfolios;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>Handle rules and the look's named choices for the Studio portfolio (docs/PORTFOLIO.md).</summary>
public sealed class PortfolioRulesTests
{
    [Theory]
    [InlineData("ab", false)]
    [InlineData("abc", true)]
    [InlineData("lens.and_light2", true)]
    [InlineData(".lens", false)]
    [InlineData("lens.", false)]
    [InlineData("le..ns", false)]
    [InlineData("12345", false)]
    [InlineData("lens-light", false)]
    [InlineData("lens light", false)]
    [InlineData("abcdefghijabcdefghijabcdefghij", true)]
    [InlineData("abcdefghijabcdefghijabcdefghijk", false)]
    public void Shape(string handle, bool ok) => Assert.Equal(ok, PortfolioHandles.ShapeProblem(handle) is null);

    [Theory]
    [InlineData("@Lens.Studio ", "lens.studio")]
    [InlineData("ABC", "abc")]
    public void Normalize(string raw, string expected) => Assert.Equal(expected, PortfolioHandles.Normalize(raw));

    [Theory]
    [InlineData("pricing")]
    [InlineData("admin")]
    [InlineData("feed")]
    [InlineData("templates")]
    [InlineData("invites_blog")]
    [InlineData("the.invitesblog.team")]
    [InlineData("support")]
    public void Reserved_words_and_site_routes_are_refused(string handle) => Assert.True(PortfolioHandles.IsReserved(handle));

    [Fact]
    public void Ordinary_names_are_not_reserved() => Assert.False(PortfolioHandles.IsReserved("aisha.photography"));

    [Theory]
    [InlineData("Lens & Light Studio", "lens.light.studio")]
    [InlineData("Café Rosé", "cafe.rose")]
    [InlineData("A", "astudio")]
    [InlineData("", "my.studio")]
    public void A_suggestion_comes_from_the_name(string name, string expected)
    {
        var s = PortfolioHandles.FromName(name);
        Assert.Equal(expected, s);
        Assert.Null(PortfolioHandles.ShapeProblem(s));
    }

    [Fact]
    public void Every_theme_and_accent_is_a_real_colour_and_oxblood_comes_first()
    {
        Assert.Equal("oxblood", PortfolioChoices.Themes[0].Key);
        Assert.All(PortfolioChoices.Themes.SelectMany(t => new[] { t.Bg, t.Surface, t.Text, t.Muted, t.Accent })
            .Concat(PortfolioChoices.Accents.Select(a => a.Color).OfType<string>()), c => Assert.Matches("^#[0-9A-F]{6}$", c));
        Assert.Equal(PortfolioChoices.Themes.Count, PortfolioChoices.Themes.Select(t => t.Key).Distinct().Count());
        Assert.Equal("#B8892F", PortfolioChoices.AccentColour("oxblood", "gold"));
        Assert.Equal("#7A0C1E", PortfolioChoices.AccentColour("oxblood", "theme"));
        Assert.Equal("oxblood", PortfolioChoices.Theme("nope").Key);
    }

    [Theory]
    [InlineData(3, 0, 2, 0, "3 photos added. 2 were added by guests and can't be used.")]
    [InlineData(1, 1, 1, 1, "1 photo added. 1 was already there. 1 was added by a guest and can't be used. Videos can't be added yet.")]
    [InlineData(0, 0, 0, 0, "No photos added.")]
    public void Added_message_is_plain(int added, int already, int guest, int video, string expected) =>
        Assert.Equal(expected, PortfolioService.AddedMessage(added, already, guest, video));
}
