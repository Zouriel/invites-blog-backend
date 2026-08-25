using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Infrastructure.Templates;
using InvitesBlog.TemplateCompiler;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Field/image-slot extraction dedupe: the same path used on many elements must produce ONE builder
/// field/slot (case-insensitive), preferring the occurrence carrying the label/type metadata. Exercised
/// through the pure <see cref="RawTemplatePackager.BuildManifest"/> (no storage/injector side effects).
/// </summary>
public class RawTemplatePackagerTests
{
    private static RawTemplatePackager Packager() => new(Substitute.For<IStorageService>());

    private static string Page(string body) =>
        $"<!doctype html><html><head><style>.x{{}}</style></head><body>{body}</body></html>";

    private static TemplateManifest Manifest(string body) =>
        Packager().BuildManifest("slug", "1.0.0", Page(body));

    [Fact]
    public void Field_repeated_on_four_elements_yields_exactly_one_field()
    {
        var m = Manifest(
            "<h1 data-var=\"event.day\"></h1>" +
            "<span data-var=\"event.day\"></span>" +
            "<div data-var=\"event.day\"></div>" +
            "<footer data-var=\"event.day\"></footer>");

        Assert.Single(m.Fields);
        Assert.Equal("event.day", m.Fields[0].Key);
    }

    [Fact]
    public void Field_dedupe_is_case_insensitive()
    {
        var m = Manifest(
            "<h1 data-var=\"event.day\"></h1>" +
            "<span data-var=\"event.Day\"></span>");

        Assert.Single(m.Fields);
        Assert.Equal("event.day", m.Fields[0].Key); // first-seen casing wins
    }

    [Fact]
    public void Labeled_occurrence_wins_over_labelless_duplicates()
    {
        // First occurrence has NO label; a later one does — the authored label must survive.
        var m = Manifest(
            "<h1 data-var=\"event.day\"></h1>" +
            "<span data-var=\"event.day\" data-field-label=\"The big day\"></span>" +
            "<div data-var=\"event.day\"></div>");

        var field = Assert.Single(m.Fields);
        Assert.Equal("The big day", field.Label);
    }

    [Fact]
    public void Typed_occurrence_wins_and_is_independent_of_label()
    {
        // "tagline" infers as plain text; label comes from one element, the explicit type from another —
        // each prefers its metadata-bearing occurrence over the label-less/inferred defaults.
        var m = Manifest(
            "<p data-var=\"event.tagline\"></p>" +
            "<p data-var=\"event.tagline\" data-field-label=\"A tagline\"></p>" +
            "<p data-var=\"event.tagline\" data-field-type=\"textarea\"></p>");

        var field = Assert.Single(m.Fields);
        Assert.Equal("A tagline", field.Label);
        Assert.Equal("textarea", field.Type); // explicit type wins over the inferred "text"
    }

    [Fact]
    public void Image_slot_dedupe_prefers_labeled_occurrence()
    {
        var m = Manifest(
            "<img data-src=\"event.hero\">" +
            "<img data-src=\"event.Hero\" data-slot-label=\"Hero photo\">" +
            "<img data-src=\"event.hero\">");

        var slot = Assert.Single(m.ImageSlots);
        Assert.Equal("event.hero", slot.Key);
        Assert.Equal("Hero photo", slot.Label);
    }

    [Fact]
    public void Href_paths_dedupe_by_path_too()
    {
        var m = Manifest(
            "<a data-href=\"rsvp.link\">RSVP</a>" +
            "<a data-href=\"rsvp.link\">RSVP again</a>");

        var field = Assert.Single(m.Fields);
        Assert.Equal("rsvp.link", field.Key);
        Assert.Equal("url", field.Type); // href leaves infer as url
    }

    [Fact]
    public void Data_type_wins_over_the_legacy_data_field_type_alias()
    {
        var m = Manifest("<p data-var=\"event.note\" data-type=\"textarea\" data-field-type=\"text\"></p>");

        Assert.Equal("textarea", Assert.Single(m.Fields).Type);
    }

    [Fact]
    public void Select_carries_its_options_through_to_the_manifest()
    {
        var m = Manifest(
            "<span data-var=\"event.dressCode\" data-type=\"select\" data-options=\"Formal, Casual, Black Tie\"></span>");

        var field = Assert.Single(m.Fields);
        Assert.Equal("select", field.Type);
        Assert.Equal(new[] { "Formal", "Casual", "Black Tie" }, field.Options);
    }

    [Fact]
    public void Select_options_may_be_authored_as_a_json_array()
    {
        var m = Manifest(
            "<span data-var=\"event.dressCode\" data-type=\"select\" data-options='[\"Formal\",\"Casual\"]'></span>");

        Assert.Equal(new[] { "Formal", "Casual" }, Assert.Single(m.Fields).Options);
    }

    [Fact]
    public void Select_without_options_is_rejected()
    {
        var ex = Assert.Throws<BusinessRuleException>(
            () => Manifest("<span data-var=\"event.dressCode\" data-type=\"select\"></span>"));

        Assert.Equal("template_select_missing_options", ex.ErrorCode);
    }

    [Fact]
    public void Non_select_fields_carry_no_options()
    {
        var m = Manifest("<span data-var=\"event.title\" data-options=\"A,B\"></span>");

        Assert.Null(Assert.Single(m.Fields).Options);
    }

    [Fact]
    public void Multi_image_slot_carries_multiple_and_its_count_bounds()
    {
        var m = Manifest(
            "<img data-src=\"event.gallery\" data-multiple=\"true\" data-min-images=\"2\" data-max-images=\"8\">");

        var slot = Assert.Single(m.ImageSlots);
        Assert.True(slot.Multiple);
        Assert.Equal(2, slot.MinImages);
        Assert.Equal(8, slot.MaxImages);
    }

    [Fact]
    public void Single_image_slot_defaults_to_not_multiple_and_unbounded()
    {
        var m = Manifest("<img data-src=\"event.hero\" data-min-images=\"3\">");

        var slot = Assert.Single(m.ImageSlots);
        Assert.False(slot.Multiple);
        Assert.Null(slot.MinImages); // bounds are meaningless without data-multiple
        Assert.Null(slot.MaxImages);
    }

    [Fact]
    public void Role_scope_on_a_field_or_slot_declares_the_role()
    {
        var m = Manifest(
            "<h2 data-var=\"groom.name\" data-role-scope=\"groom\"></h2>" +
            "<img data-src=\"bride.photo\" data-role-scope=\"Bride\">" +
            "<h1 data-var=\"event.title\"></h1>");

        Assert.Equal(new[] { "groom", "bride" }, m.Roles);

        var groom = m.RoleDefinitions.Single(r => r.Slug == "groom");
        Assert.Equal(new[] { "groom.name" }, groom.Fields);
        Assert.Empty(groom.ImageSlots);

        var bride = m.RoleDefinitions.Single(r => r.Slug == "bride");
        Assert.Equal(new[] { "bride.photo" }, bride.ImageSlots);

        // Unscoped entries are shared — they belong to no role definition.
        Assert.DoesNotContain(m.RoleDefinitions, r => r.Fields.Contains("event.title"));
        Assert.Null(m.Fields.Single(f => f.Key == "event.title").RoleScope);
    }

    [Fact]
    public void Declared_roles_meta_is_unioned_with_scoped_roles()
    {
        var m = Packager().BuildManifest("slug", "1.0.0",
            "<!doctype html><html><head><meta name=\"ib-roles\" content=\"bride, groom, guest\"></head>" +
            "<body><h2 data-var=\"usher.name\" data-role-scope=\"usher\"></h2></body></html>");

        Assert.Equal(new[] { "bride", "groom", "guest", "usher" }, m.Roles);
    }

    [Fact]
    public void Theme_extracts_the_declared_ib_custom_properties_with_their_defaults()
    {
        var m = Packager().BuildManifest("slug", "1.0.0",
            "<!doctype html><html><head><meta name=\"ib-fonts\" content=\"Lora, Inter\"><style>" +
            ":root{--ib-accent:#c9a227;--ib-bg:#0b0b0f;--ib-text:#f6f2e8;--ib-heading-font:Lora,serif}" +
            "</style></head><body></body></html>");

        Assert.Equal("#c9a227", m.Theme.AccentColor);
        Assert.Equal("#0b0b0f", m.Theme.BackgroundColor);
        Assert.Equal("#f6f2e8", m.Theme.TextColor);
        Assert.Equal(new[] { "Lora", "Inter" }, m.Theme.Fonts);

        var accent = m.Theme.Keys.Single(k => k.Key == "accentColor");
        Assert.Equal("--ib-accent", accent.CssVar);
        Assert.Equal("color", accent.Type);
        Assert.Equal("#c9a227", accent.Default);

        var heading = m.Theme.Keys.Single(k => k.Key == "headingFont");
        Assert.Equal("font", heading.Type);
        Assert.Equal("Heading font", heading.Label);
        Assert.Equal("Accent colour", accent.Label);
    }

    [Fact]
    public void Theme_keeps_the_first_declaration_so_media_query_overrides_dont_win()
    {
        var m = Packager().BuildManifest("slug", "1.0.0",
            "<html><head><style>:root{--ib-accent:#111}@media(prefers-color-scheme:dark){:root{--ib-accent:#eee}}</style></head><body></body></html>");

        Assert.Equal("#111", m.Theme.AccentColor);
        Assert.Single(m.Theme.Keys);
    }

    [Fact]
    public void Every_role_can_override_every_theme_key()
    {
        var m = Packager().BuildManifest("slug", "1.0.0",
            "<html><head><meta name=\"ib-roles\" content=\"bride,groom\">" +
            "<style>:root{--ib-accent:#c9a227;--ib-bg:#fff}</style></head><body></body></html>");

        Assert.All(m.RoleDefinitions,
            r => Assert.Equal(new[] { "accentColor", "backgroundColor" }, r.ThemeKeys));
    }

    // --- Safety scan: HTML/CSS only, for EVERY author including admins -------------------------

    [Theory]
    [InlineData("<script>alert(1)</script>", "template_script_not_allowed")]
    [InlineData("<script src=\"https://cdn.example/x.js\"></script>", "template_script_not_allowed")]
    [InlineData("<button onclick=\"go()\">x</button>", "template_inline_handler_not_allowed")]
    [InlineData("<body ONLOAD='go()'></body>", "template_inline_handler_not_allowed")]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", "template_javascript_uri_not_allowed")]
    [InlineData("<meta http-equiv=\"refresh\" content=\"0;url=/x\">", "template_meta_refresh_not_allowed")]
    [InlineData("<link rel=\"stylesheet\" href=\"x.css\">", "template_not_self_contained")]
    public void The_scan_hard_rejects_anything_executable(string markup, string expectedCode)
    {
        var ex = Assert.Throws<BusinessRuleException>(
            () => RawTemplatePackager.EnsureSelfContainedAndSafe(Page(markup)));

        Assert.Equal(expectedCode, ex.ErrorCode);
    }

    [Fact]
    public void A_plain_html_and_css_template_passes_the_scan()
    {
        // "on" inside an ordinary word or attribute value must not be mistaken for a handler.
        RawTemplatePackager.EnsureSelfContainedAndSafe(
            Page("<p class=\"lonely\" data-var=\"event.season\">A long onward journey</p>"));
    }

    [Fact]
    public void A_template_over_the_hard_size_limit_is_rejected()
    {
        var huge = Page(new string('x', RawTemplatePackager.MaxTemplateBytes + 1));

        var ex = Assert.Throws<BusinessRuleException>(() => RawTemplatePackager.EnsureSelfContainedAndSafe(huge));

        Assert.Equal("template_too_large", ex.ErrorCode);
    }

    [Fact]
    public void A_template_over_the_soft_budget_but_under_the_ceiling_still_passes()
    {
        var big = Page(new string('x', RawTemplatePackager.RecommendedTemplateBytes + 1));

        RawTemplatePackager.EnsureSelfContainedAndSafe(big);
    }

    [Fact]
    public void Renderer_fills_all_elements_sharing_a_path()
    {
        // The single deduped value must reach EVERY element carrying the path — the injector selects all
        // matches (querySelectorAll), not just the first (querySelector).
        Assert.Contains("querySelectorAll('[data-var]')", TemplateInjector.Js);
        Assert.Contains("querySelectorAll('[data-href]')", TemplateInjector.Js);
        Assert.Contains("querySelectorAll('[data-src]')", TemplateInjector.Js);
        Assert.DoesNotContain("querySelector('[data-var]')", TemplateInjector.Js);
    }

    // ----- the card image -----

    /// <summary>A storage double that records what was written and where.</summary>
    private static (RawTemplatePackager Sut, Dictionary<string, (byte[] Content, string Type)> Written) Recording()
    {
        var written = new Dictionary<string, (byte[], string)>();
        var storage = Substitute.For<IStorageService>();
        storage.PutAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                written[ci.ArgAt<string>(0)] = (ci.ArgAt<byte[]>(1), ci.ArgAt<string>(2));
                return Task.FromResult("/assets/" + ci.ArgAt<string>(0));
            });
        storage.PublicUrl(Arg.Any<string>()).Returns(ci => "/assets/" + ci.ArgAt<string>(0));
        return (new RawTemplatePackager(storage), written);
    }

    [Fact]
    public async Task A_shipped_poster_is_published_beside_the_package()
    {
        // The gallery shows this still instead of rendering the whole template to act as a thumbnail.
        var (sut, written) = Recording();
        var poster = new byte[] { 0x52, 0x49, 0x46, 0x46, 1, 2, 3, 4 };

        var result = await sut.PublishAsync("nice-one", "1.0.0", Page("hi"), allowScripts: true, poster: poster);

        var key = written.Keys.Single(k => k.Contains("poster."));
        Assert.Matches(@"^templates/nice-one@1\.0\.0/poster\.[0-9a-f]{8}\.webp$", key);
        Assert.Equal("/assets/" + key, result.PosterUrl);
        Assert.Equal("image/webp", written[key].Type);
        Assert.Equal(poster, written[key].Content);
    }

    [Fact]
    public async Task Changing_a_poster_changes_its_url()
    {
        // The package path is versioned, but a poster can be corrected without a version bump. A fixed
        // filename meant the edge kept serving the old picture for hours after the bytes had changed.
        var (sut, _) = Recording();

        var first = await sut.PublishAsync("x", "1.0.0", Page("hi"), allowScripts: true, poster: [1, 2, 3]);
        var same = await sut.PublishAsync("x", "1.0.0", Page("hi"), allowScripts: true, poster: [1, 2, 3]);
        var changed = await sut.PublishAsync("x", "1.0.0", Page("hi"), allowScripts: true, poster: [9, 9, 9]);

        Assert.Equal(first.PosterUrl, same.PosterUrl);        // same bytes, same URL — no churn
        Assert.NotEqual(first.PosterUrl, changed.PosterUrl);  // new bytes, new URL — nothing to go stale
    }

    [Fact]
    public async Task A_template_with_no_poster_publishes_none()
    {
        // Optional by design: the gallery falls back to rendering the template, which still works.
        var (sut, written) = Recording();

        var result = await sut.PublishAsync("plain", "1.0.0", Page("hi"), allowScripts: true);

        Assert.Null(result.PosterUrl);
        Assert.DoesNotContain(written.Keys, k => k.EndsWith("poster.webp"));
    }

    [Fact]
    public async Task An_empty_poster_is_not_published()
    {
        // A zero-byte file would publish a URL that resolves to a broken image — worse than none.
        var (sut, written) = Recording();

        var result = await sut.PublishAsync("plain", "1.0.0", Page("hi"), allowScripts: true, poster: []);

        Assert.Null(result.PosterUrl);
        Assert.DoesNotContain(written.Keys, k => k.EndsWith("poster.webp"));
    }

    [Fact]
    public void Expanding_a_gallery_a_second_time_does_not_multiply_it()
    {
        // apply() runs once for the inline payload and again for every __inviteData the host posts
        // (the editor posts one per edit). A clone carries data-src and data-multiple exactly like the
        // element it was cloned from, so the second pass used to expand the clones too: six photos
        // became thirty-six, a third pass two hundred and sixteen. In the invitation that read as the
        // same handful of photos cycling past over and over before the section would let you scroll on.
        //
        // Verified in a browser against both versions: 6 -> 36 -> 216 before, 6 -> 6 -> 6 after. There
        // is no JS engine here to re-run that, so this pins the two halves of the guard instead — a
        // clone is never itself expanded, and last pass's clones are cleared before this pass builds.
        Assert.Contains("if (img.getAttribute('data-gallery-clone')) continue;", TemplateInjector.Js);
        Assert.Contains("clone.setAttribute('data-gallery-clone', gid);", TemplateInjector.Js);
        Assert.Contains("removeChild(stale[x])", TemplateInjector.Js);
    }
}
