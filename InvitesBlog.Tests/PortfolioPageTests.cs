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
        // Picking a photo or typing clears the "tap the photo" hint, so it doesn't sit there after they did.
        Assert.Contains("function clearErr()", html);
    }

    private static PublicPortfolioDto Full(PortfolioContactDto contact, string pos = "bottom-left", string size = "medium")
    {
        var photos = new List<PortfolioPhotoDto> { new(Guid.NewGuid(), "/a/1.jpg", "/a/1t.jpg", 400, 300, null, null), new(Guid.NewGuid(), "/a/2.jpg", "/a/2t.jpg", 300, 400, null, null) };
        return new PublicPortfolioDto("lens", "https://invites.blog/@lens", "Lens", null, "/a/logo.png", "/a/c.jpg", "oxblood-dark", "theme", "classic", contact,
            [
                new("cover", null, null, "/a/c.jpg", null, null, null, null),
                new("collection", null, null, null, new PublicCollectionDto(Guid.NewGuid(), "Moments", "slideshow", null, photos), null, null, null),
                new("collection", null, null, null, new PublicCollectionDto(Guid.NewGuid(), "Weddings", "grid", null, photos), null, null, null),
                new("contact", "Get in touch", null, null, null, null, null, null),
            ], pos, size);
    }

    private static readonly PortfolioContactDto Both = new("+9607000000", null, "lens", null, "hi@example.com", true);

    private static string Rule(string html, string selector)
    {
        var m = System.Text.RegularExpressions.Regex.Match(html, "(?<=[}\\n])" + System.Text.RegularExpressions.Regex.Escape(selector) + "\\{[^}]*\\}");
        Assert.True(m.Success, $"no rule {selector}");
        return m.Value;
    }

    [Fact]
    public void The_slideshow_letterbox_is_the_page_background_never_a_dark_plate()
    {
        var html = PortfolioPage.Render(Full(Both), "n", "https://invites.blog", false);
        // Owner: big black bars around photos. A photo that doesn't fill the frame shows the page behind it.
        var slide = Rule(html, ".slide");
        Assert.Contains("background:transparent", slide);
        Assert.DoesNotContain("#0c0c0d", html);
        Assert.DoesNotMatch("background:(#0|#1|black|rgba\\(0,0,0)", slide);
        Assert.Contains("object-fit:contain", Rule(html, ".slide img"));
        // The arrows are the theme's own surface, not a dark disc.
        Assert.Contains("background:var(--surface)", Rule(html, ".show-btn"));
        // Cross-fade between stacked photos, only once the script runs.
        Assert.Contains("transition:opacity .6s ease-in-out", Rule(html, ".js .slide"));
        Assert.Contains("1 / 2", html);
    }

    [Fact]
    public void Things_wait_hidden_for_their_reveal_only_when_the_script_runs()
    {
        var html = PortfolioPage.Render(Full(Both), "nonce1", "https://invites.blog", false);
        var css = html[html.IndexOf("<style>", StringComparison.Ordinal)..html.IndexOf("</style>", StringComparison.Ordinal)];
        // Every rule that hides a .rv before it is revealed is scoped to html.js: without the script, all shows.
        var hiding = System.Text.RegularExpressions.Regex.Matches(css, "(?<=[}\\n])([^{}]*\\.rv[^{}]*)\\{([^}]*)\\}")
            .Where(m => m.Groups[2].Value.Contains("opacity:0")).ToList();
        Assert.NotEmpty(hiding);
        Assert.All(hiding, m => Assert.All(m.Groups[1].Value.Split(','), sel => Assert.StartsWith(".js ", sel.Trim())));
        // The class is set in the head (no flash) by a script the CSP allows, with a way back if the page script never starts.
        Assert.Contains("<script nonce=\"nonce1\">document.documentElement.classList.add('js');", html);
        Assert.Contains("if(!window.__rv)document.documentElement.classList.remove('js')", html);
        // The page script sets up the reveal before anything else it does, so a later error can't leave things hidden.
        var script = html.IndexOf("<script nonce=\"nonce1\">window.__pf=", StringComparison.Ordinal);
        Assert.True(script > 0);
        var rv = html.IndexOf("window.__rv=1", script, StringComparison.Ordinal);
        Assert.True(rv > 0 && rv < html.IndexOf("var P=window.__pf", script, StringComparison.Ordinal));
        Assert.Contains("class=\"tile rv\"", html);
        Assert.Contains("<h2 class=\"rv\">Moments</h2>", html);
    }

    [Fact]
    public void Reduced_motion_turns_every_animation_off()
    {
        var html = PortfolioPage.Render(Full(Both), "n", "https://invites.blog", false);
        var at = html.IndexOf("@media(prefers-reduced-motion:reduce){", StringComparison.Ordinal);
        Assert.True(at > 0);
        var block = html[at..html.IndexOf("</style>", at, StringComparison.Ordinal)];
        Assert.Contains(".hero-img,.hero-in>*,.rv.in,.lb{animation:none}", block);
        Assert.Contains(".js .rv:not(.in){opacity:1}", block);
        Assert.Contains(".js .slide,.js .slide.on{transition:none}", block);
        Assert.Contains("matchMedia('(prefers-reduced-motion: reduce)')", html);
        // Hover lift only where there is a real pointer.
        Assert.Contains("@media(hover:hover) and (pointer:fine){", html);
    }

    [Fact]
    public void Get_in_touch_is_a_full_width_band_with_the_form_beside_the_buttons_on_a_wide_screen()
    {
        var html = PortfolioPage.Render(Full(Both), "n", "https://invites.blog", false);
        Assert.Contains("<section class=\"band\" id=\"contact\"><div class=\"contact split\">", html);
        Assert.Contains("@media(min-width:900px){.contact.split{grid-template-columns:minmax(0,5fr) minmax(0,6fr)", html);
        Assert.Contains("background:var(--surface)", Rule(html, ".band"));
        // Only a form, or only buttons: centred on its own.
        var formOnly = PortfolioPage.Render(Full(new PortfolioContactDto(null, null, null, null, null, true)), "n", "https://invites.blog", false);
        Assert.Contains("<div class=\"contact solo\">", formOnly);
        var buttonsOnly = PortfolioPage.Render(Full(Both with { Enquiries = false }), "n", "https://invites.blog", false);
        Assert.Contains("<div class=\"contact solo\">", buttonsOnly);
        Assert.DoesNotContain("id=\"enq\"", buttonsOnly);
    }

    [Theory]
    [InlineData("bottom-left", false)]
    [InlineData("bottom-center", false)]
    [InlineData("center", false)]
    [InlineData("top-left", true)]
    [InlineData("top-center", true)]
    public void The_logo_sits_where_the_studio_put_it(string pos, bool top)
    {
        var html = PortfolioPage.Render(Full(Both, pos, "large"), "n", "https://invites.blog", false);
        Assert.Contains($"<header class=\"hero pos-{pos} logo-large\">", html);
        var logo = "<img class=\"logo\" src=\"/a/logo.png\"";
        Assert.Equal(top, html.Contains("<div class=\"hero-top\">" + logo));
        Assert.Equal(!top, html.Contains("<div class=\"hero-in\">" + logo));
        // Unknown values fall back to the original look.
        Assert.Contains("<header class=\"hero pos-bottom-left logo-medium\">", PortfolioPage.Render(Full(Both, "sideways", "huge"), "n", "https://invites.blog", false));
    }

    [Fact]
    public void A_transparent_logo_is_drawn_as_it_is_with_no_crop_or_plate_behind_it()
    {
        var html = PortfolioPage.Render(Full(Both), "n", "https://invites.blog", false);
        var logo = Rule(html, ".logo");
        Assert.Contains("object-fit:contain", logo);
        Assert.DoesNotContain("border-radius", logo);
        Assert.DoesNotContain("background", logo);
        Assert.DoesNotContain("border:", logo);
    }

    [Fact]
    public void Every_font_pair_asks_google_for_weights_it_has()
    {
        var html = PortfolioPage.Render(Page(null) with { Font = "editorial" }, "n", "https://invites.blog", false);
        Assert.Contains("family=DM+Serif+Display:wght@400&", html);
        Assert.Contains("--head-weight:400", html);
    }
}
