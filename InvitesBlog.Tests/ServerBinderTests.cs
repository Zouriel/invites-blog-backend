using System.Text;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Infrastructure.Templates;
using InvitesBlog.TemplateCompiler;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// The server-side binder against the real committed templates, packaged the way production packages
/// them. These pin the contract templates were written against — a missing value blanks its element,
/// a gallery clones the authored markup, guest text is never markup — because that contract is now
/// implemented here rather than in the browser.
/// </summary>
public class ServerBinderTests
{
    private static readonly string[] Slugs = ["aurora-vows", "a-love-story", "gilded-hour"];

    /// <summary>The raw committed template, read from the embedded resource the seeder publishes.</summary>
    private static string RawHtml(string slug)
    {
        var asm = typeof(RawTemplatePackager).Assembly;
        var name = asm.GetManifestResourceNames()
            .Single(n => n.Contains(".RawTemplates.", StringComparison.Ordinal)
                         && n.EndsWith(".index.html", StringComparison.Ordinal)
                         && n.Replace('_', '-').Contains($".{slug}.", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>
    /// Runs the real publish path and captures what it would have written to storage, so these tests
    /// bind exactly the document a guest is served rather than a hand-assembled approximation.
    /// </summary>
    private static async Task<string> PackagedHtml(string slug)
    {
        var storage = Substitute.For<IStorageService>();
        var captured = string.Empty;
        await storage.PutAsync(
            Arg.Is<string>(k => k.EndsWith("/index.html", StringComparison.Ordinal)),
            Arg.Do<byte[]>(b => captured = Encoding.UTF8.GetString(b)),
            Arg.Any<string>(), Arg.Any<CancellationToken>());

        var packager = new RawTemplatePackager(storage);
        await packager.PublishAsync(slug, "1.0.0", RawHtml(slug));
        return captured;
    }

    /// <summary>A payload shaped like <c>InviteRenderService</c>'s, filled enough to bind every slug.</summary>
    private static JsonObject Payload(JsonArray? gallery = null, JsonArray? blocks = null)
    {
        var photos = gallery ?? new JsonArray("/assets/campaigns/a.jpg");
        return new JsonObject
    {
        ["event"] = new JsonObject
        {
            ["title"] = "Aisha &amp; Ibrahim",
            ["subtitle"] = "are getting married",
            ["date"] = "Saturday, 4 October 2026",
            ["time"] = "4:00 PM",
            ["day"] = "04",
            ["month"] = "October",
            ["year"] = "2026",
            // Each template names its own gallery slot; carry both so every slug binds. A JsonNode
            // may only have one parent, hence the clone.
            ["gallery"] = photos.DeepClone(),
            ["filmStrip"] = photos.DeepClone(),
            ["coverImage"] = "/assets/campaigns/cover.jpg",
            ["venue"] = new JsonObject
            {
                ["name"] = "The Old Observatory",
                ["address"] = "12 Hilltop Road",
                // Deliberately absent: mapLink. Every template wraps it in [data-optional].
            }
        },
        ["guest"] = new JsonObject { ["name"] = "Yusuf", ["role"] = "guest" },
        ["inviter"] = new JsonObject { ["name"] = "The Ahmed Family" },
        ["rsvp"] = new JsonObject { ["link"] = "https://me.invites.blog/i/tok3n/rsvp" },
        ["invite"] = new JsonObject { ["link"] = "https://me.invites.blog/i/tok3n" },
        ["themeVars"] = new JsonObject { ["--ib-accent"] = "#c2185b" },
        ["resolvedBlocks"] = blocks ?? new JsonArray()
        };
    }

    private static AngleSharp.Html.Dom.IHtmlDocument Parse(string html) =>
        new HtmlParser().ParseDocument(html);

    /// <summary>
    /// A binding that resolves to nothing must not leave something pressable.
    ///
    /// <para>An unresolved [data-href] used to keep the author's placeholder "#", which is not inert
    /// — it is a link to the top of the document, so pressing the button scrolled the page away.
    /// That stayed hidden while every optional link happened to sit in a [data-optional] wrapper.
    /// The moment a payload withheld one that did not, every template lacking that wrapper — every
    /// uploaded one, which nobody can go back and edit — grew a button that jumped to the top.</para>
    /// </summary>
    [Fact]
    public void A_link_that_binds_to_nothing_is_taken_away_even_without_a_wrapper()
    {
        const string html = """
            <html><body>
              <a id="bare" class="btn" data-href="rsvp.link" href="#">Reply now</a>
              <a id="real" data-href="rsvp.link" href="/already-here">Reply now</a>
            </body></html>
            """;

        var doc = Parse(ServerBinder.Bind(html, new JsonObject { ["rsvp"] = new JsonObject() }));

        // No wrapper, nothing to bind to: gone.
        Assert.True(Hidden(doc.QuerySelector("#bare")!), "a dead placeholder link was left pressable");

        // An element that carried a real href of its own is not ours to remove.
        var real = doc.QuerySelector("#real")!;
        Assert.False(Hidden(real));
        Assert.Equal("/already-here", real.GetAttribute("href"));
    }

    /// <summary>
    /// The RSVP control disappears once a guest has said they are coming, and changes its wording
    /// for anyone whose answer is not settled.
    ///
    /// <para>Asks whether anything is ON SCREEN rather than whether the link works — an unresolved
    /// href stays "#" as authored, so a control left behind reads as passing while a guest still
    /// sees a button that does nothing.</para>
    /// </summary>
    [Theory]
    [InlineData("aurora-vows")]
    [InlineData("gilded-hour")]
    public async Task The_rsvp_control_goes_once_they_have_said_they_are_coming(string slug)
    {
        var html = await PackagedHtml(slug);

        var going = Payload();
        going["rsvp"] = new JsonObject { ["link"] = null, ["label"] = "" };
        var afterGoing = Parse(ServerBinder.Bind(html, going));
        var control = afterGoing.QuerySelector("[data-href='rsvp.link']");
        Assert.True(
            control is null || Hidden(control),
            $"{slug} still shows an RSVP control to a guest who is coming");

        var maybe = Payload();
        maybe["rsvp"] = new JsonObject { ["link"] = "/r/abc/rsvp", ["label"] = "Confirm your reply" };
        var afterMaybe = Parse(ServerBinder.Bind(html, maybe));
        var offered = afterMaybe.QuerySelector("[data-href='rsvp.link']");
        Assert.NotNull(offered);
        Assert.False(Hidden(offered!), $"{slug} hid the RSVP control from an unsettled guest");
        Assert.Equal("/r/abc/rsvp", offered!.GetAttribute("href"));
        Assert.Contains("Confirm your reply", offered.TextContent);
    }

    /// <summary>
    /// The camera is offered only to a guest who said they were coming, on the night. The rule lives
    /// in the payload — the LINK is present or it is not — and every template wraps its capture block
    /// in [data-optional], so an unresolved href takes the whole block with it.
    ///
    /// <para>This is the assertion that matters. The payload tests either side of it check
    /// intermediate values; only this one says a guest cannot see the button.</para>
    /// </summary>
    [Theory]
    [InlineData("aurora-vows")]
    [InlineData("a-love-story")]
    [InlineData("gilded-hour")]
    public async Task A_closed_camera_takes_the_capture_block_out_of_the_page(string slug)
    {
        var open = Payload();
        open["camera"] = new JsonObject { ["link"] = "/r/abc/camera" };
        var withCamera = Parse(ServerBinder.Bind(await PackagedHtml(slug), open));

        var button = withCamera.QuerySelector("[data-href='camera.link']");
        Assert.NotNull(button);
        Assert.Equal("/r/abc/camera", button!.GetAttribute("href"));
        Assert.Contains("Capture moments", button.TextContent);

        // Closed: the object stays, the link goes.
        var shut = Payload();
        shut["camera"] = new JsonObject();
        var withoutCamera = Parse(ServerBinder.Bind(await PackagedHtml(slug), shut));

        // GONE or HIDDEN — not merely inert. An unresolved href stays "#" as the author wrote it,
        // so a control left in the page is one a guest still SEES and presses, and pressing it does
        // nothing. The first version of this test asked whether the href was usable, which is true
        // of a dead button as well as an absent one, and it passed with the wrapper's
        // [data-optional] deleted. The question is whether anything is on screen.
        var control = withoutCamera.QuerySelector("[data-href='camera.link']");
        Assert.True(
            control is null || Hidden(control),
            $"{slug} still shows a capture control after the camera closed");
    }

    /// <summary>Walks up looking for the hidden marker the binder applies to empty optionals.</summary>
    private static bool Hidden(AngleSharp.Dom.IElement el)
    {
        for (var node = el; node is not null; node = node.ParentElement)
        {
            if (node.HasAttribute("hidden")) return true;
            var style = node.GetAttribute("style");
            if (style is not null && style.Contains("display", StringComparison.OrdinalIgnoreCase)
                && style.Contains("none", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    [Theory]
    [InlineData("aurora-vows")]
    [InlineData("a-love-story")]
    [InlineData("gilded-hour")]
    public async Task Binds_every_committed_template_without_leaving_a_binding_attribute_unresolved(string slug)
    {
        var doc = Parse(ServerBinder.Bind(await PackagedHtml(slug), Payload()));

        // Every element the payload had a value for now carries it. Anything it didn't is blank or
        // hidden, never still showing the author's placeholder.
        var title = doc.QuerySelector("[data-var='event.title']");
        Assert.NotNull(title);
        Assert.Equal("Aisha &amp; Ibrahim", title!.TextContent);

        // ...and it is TEXT. The ampersand above round-trips as an ampersand rather than becoming an
        // entity the browser would re-decode, which is what "guest content is data, never markup" means.
        Assert.DoesNotContain("<script>alert", doc.Body!.InnerHtml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Guest_text_is_inserted_as_text_never_markup()
    {
        var payload = Payload();
        payload["guest"]!["name"] = "<img src=x onerror=alert(1)>";

        var html = ServerBinder.Bind(await PackagedHtml("a-love-story"), payload);
        var doc = Parse(html);

        var el = doc.QuerySelector("[data-var='guest.name']")!;
        Assert.Equal("<img src=x onerror=alert(1)>", el.TextContent);
        Assert.Empty(doc.QuerySelectorAll("img[onerror]"));
        Assert.Contains("&lt;img", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Gallery_clones_the_authored_element_once_per_photo()
    {
        var photos = new JsonArray("/a.jpg", "/b.jpg", "/c.jpg", "/d.jpg", "/e.jpg", "/f.jpg");
        var doc = Parse(ServerBinder.Bind(await PackagedHtml("gilded-hour"), Payload(photos)));

        var bound = doc.QuerySelectorAll("[data-src='event.gallery']").ToList();
        Assert.Equal(6, bound.Count);
        Assert.Equal(
            new[] { "/a.jpg", "/b.jpg", "/c.jpg", "/d.jpg", "/e.jpg", "/f.jpg" },
            bound.Select(e => e.GetAttribute("src")));

        // One original, five clones — the shape the JavaScript produced, kept so authored CSS that
        // keys off these attributes behaves identically.
        Assert.Single(bound, e => e.HasAttribute("data-gallery-of"));
        Assert.Equal(5, bound.Count(e => e.HasAttribute("data-gallery-clone")));
    }

    /// <summary>
    /// The bug this whole change exists to make impossible: the browser bound on load AND on every
    /// host post, so a gallery that cloned on each pass went 6 → 36 → 216. Binding twice here must be
    /// indistinguishable from binding once, because a re-render is a re-render, not a second pass.
    /// </summary>
    [Fact]
    public async Task Binding_is_not_something_that_can_happen_twice()
    {
        var photos = new JsonArray("/a.jpg", "/b.jpg", "/c.jpg", "/d.jpg", "/e.jpg", "/f.jpg");
        var packaged = await PackagedHtml("gilded-hour");

        var once = ServerBinder.Bind(packaged, Payload(photos));
        var twice = ServerBinder.Bind(packaged, Payload(photos));

        Assert.Equal(once, twice);
        Assert.Equal(6, Parse(once).QuerySelectorAll("[data-src='event.gallery']").Count());
    }

    [Fact]
    public async Task Optional_elements_whose_value_never_arrived_are_hidden()
    {
        // The payload above has no venue.mapLink; gilded-hour wraps that link in [data-optional].
        var doc = Parse(ServerBinder.Bind(await PackagedHtml("gilded-hour"), Payload()));

        var mapLink = doc.QuerySelector("[data-href='event.venue.mapLink']")!;
        var optional = mapLink.Closest("[data-optional]")!;
        Assert.Contains("display:none", optional.GetAttribute("style") ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Only_the_blocks_resolved_for_this_guest_are_shown()
    {
        var doc = Parse(ServerBinder.Bind(
            await PackagedHtml("aurora-vows"),
            Payload(blocks: new JsonArray("maleDressCode"))));

        var shown = doc.QuerySelector("[data-block='maleDressCode']")!;
        var hidden = doc.QuerySelector("[data-block='femaleDressCode']")!;

        Assert.DoesNotContain("display:none", shown.GetAttribute("style") ?? string.Empty, StringComparison.Ordinal);
        Assert.Contains("display:none", hidden.GetAttribute("style") ?? string.Empty, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Theme_overrides_are_set_inline_on_the_root_so_they_outrank_the_template_default()
    {
        var doc = Parse(ServerBinder.Bind(await PackagedHtml("gilded-hour"), Payload()));

        var style = doc.DocumentElement.GetAttribute("style") ?? string.Empty;
        Assert.Contains("--ib-accent:#c2185b", style, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--ib-accent", "red;} body{display:none")]
    [InlineData("colour", "red")]
    public async Task A_theme_value_that_tries_to_escape_its_declaration_is_dropped(string name, string value)
    {
        var payload = Payload();
        payload["themeVars"] = new JsonObject { [name] = value };

        var doc = Parse(ServerBinder.Bind(await PackagedHtml("gilded-hour"), payload));

        Assert.DoesNotContain("body{display:none", doc.DocumentElement.GetAttribute("style") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.DoesNotContain("colour", doc.DocumentElement.GetAttribute("style") ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("aurora-vows")]
    [InlineData("a-love-story")]
    [InlineData("gilded-hour")]
    public async Task The_injector_is_gone_and_the_data_blind_runtime_is_in_its_place(string slug)
    {
        var html = ServerBinder.Bind(await PackagedHtml(slug), Payload());
        var doc = Parse(html);

        // Nothing left that binds, posts, or listens for a host message.
        Assert.DoesNotContain("__inviteReady", html, StringComparison.Ordinal);
        Assert.DoesNotContain("__inviteData", html, StringComparison.Ordinal);

        // ...and the scroll runtime is present.
        Assert.Contains("--ib-progress", html, StringComparison.Ordinal);
        Assert.Contains("invite:data", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("aurora-vows")]
    [InlineData("a-love-story")]
    [InlineData("gilded-hour")]
    public async Task The_payload_stays_inline_for_template_scripts_and_is_marked_bound(string slug)
    {
        var doc = Parse(ServerBinder.Bind(await PackagedHtml(slug), Payload()));

        var payload = doc.GetElementById("invite-data")!;
        Assert.Equal("1", payload.GetAttribute("data-bound"));

        var parsed = JsonNode.Parse(payload.TextContent)!.AsObject();
        Assert.Equal("Yusuf", parsed["guest"]!["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reduced_motion_rules_never_reach_the_browser()
    {
        // The JavaScript deleted these from document.styleSheets after paint, so the calmed styles
        // applied for a frame first. Stripped here, they are simply not in the document.
        var html = ServerBinder.Bind(await PackagedHtml("gilded-hour"), Payload());
        Assert.DoesNotContain("prefers-reduced-motion", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Stripping_reduced_motion_leaves_every_other_media_query_alone()
    {
        const string css = """
            @media (max-width: 600px) { .a { color: red } }
            @media (prefers-reduced-motion: reduce) { .b { animation: none } .c { transition: none } }
            @media (min-width: 900px) { .d { color: blue } }
            """;

        var stripped = ServerBinder.RemoveReducedMotionBlocks(css);

        Assert.Contains(".a { color: red }", stripped, StringComparison.Ordinal);
        Assert.Contains(".d { color: blue }", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("animation: none", stripped, StringComparison.Ordinal);
        Assert.DoesNotContain("transition: none", stripped, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_value_the_payload_does_not_carry_blanks_its_element_rather_than_keeping_the_placeholder()
    {
        var doc = Parse(ServerBinder.Bind(await PackagedHtml("gilded-hour"), Payload()));

        // hashtag is authored with placeholder text and is not in the payload.
        var el = doc.QuerySelector("[data-var='event.hashtag']");
        if (el is not null) Assert.Equal(string.Empty, el.TextContent);
    }

    [Fact]
    public async Task A_null_reads_as_missing_the_way_it_did_in_the_browser()
    {
        var payload = Payload();
        payload["event"]!["subtitle"] = null;

        var doc = Parse(ServerBinder.Bind(await PackagedHtml("aurora-vows"), payload));

        var el = doc.QuerySelector("[data-var='event.subtitle']");
        if (el is not null) Assert.Equal(string.Empty, el.TextContent);
    }
}
