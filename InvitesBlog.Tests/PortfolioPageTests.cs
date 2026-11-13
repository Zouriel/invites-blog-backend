using InvitesBlog.Api.Rendering;
using InvitesBlog.Application.Services.Portfolios;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>The server-rendered portfolio page (invites.blog/@handle): what link previews and Cloudflare see.</summary>
public sealed class PortfolioPageTests
{
    private static PublicPortfolioDto Page(string? cover, string? email = "hi@example.com") =>
        new("lens", "https://invites.blog/@lens", "Lens <Studio>", "Weddings", null, cover, "oxblood", "theme", "classic",
            new PortfolioContactDto(null, null, null, null, email, true),
            [new("cover", null, null, cover, null, null, null, null), new("contact", "Get in touch", null, null, null, null, null, null)]);

    [Fact]
    public void Link_preview_pictures_are_full_addresses_even_when_stored_ones_are_relative()
    {
        var html = PortfolioPage.Render(Page("/assets/portfolios/p/c.jpg"), "n", "https://invites.blog", false);
        Assert.Contains("<meta property=\"og:image\" content=\"https://invites.blog/assets/portfolios/p/c.jpg\">", html);
        Assert.Contains("\"image\":\"https://invites.blog/assets/portfolios/p/c.jpg\"", html);
        Assert.Contains("<title>Lens &lt;Studio&gt; (@lens)</title>", html);
    }

    [Fact]
    public void Cloudflare_is_told_not_to_rewrite_the_email_button()
    {
        var html = PortfolioPage.Render(Page(null), "n", "https://invites.blog", false);
        var on = html.IndexOf("<!--email_off-->", StringComparison.Ordinal);
        var off = html.IndexOf("<!--/email_off-->", StringComparison.Ordinal);
        var mail = html.IndexOf("mailto:hi@example.com", StringComparison.Ordinal);
        Assert.True(on >= 0 && off > mail && mail > on);
    }

    [Fact]
    public void The_remove_request_dialog_hides_what_it_hides_and_offers_the_photos_to_pick()
    {
        var html = PortfolioPage.Render(Page(null), "n", "https://invites.blog", false);
        // img{display:block} beat the hidden attribute, so an empty picture frame showed in the dialog.
        Assert.Contains("[hidden]{display:none!important}", html);
        Assert.Contains("class=\"rep-pick\"", html);
        // The visitor can't send a request that names neither a photo nor anything about it.
        Assert.Contains("Tap the photo you mean, or describe it", html);
        Assert.Contains("class=\"btn wide rep-done\"", html);
    }

    [Fact]
    public void Every_font_pair_asks_google_for_weights_it_has()
    {
        var html = PortfolioPage.Render(Page(null) with { Font = "editorial" }, "n", "https://invites.blog", false);
        Assert.Contains("family=DM+Serif+Display:wght@400&", html);
        Assert.Contains("--head-weight:400", html);
    }
}
