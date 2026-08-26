using System.Reflection;
using System.Text;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Infrastructure.Templates;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// The committed raw templates are the platform's own work. They must satisfy every rule we hold
/// community designers to — self-contained, within budget, no inline handlers or javascript: URLs,
/// and declaring a real theming surface — with ONE deliberate exception: they may carry their own
/// script, because they ship in this repository and are reviewed like any other source file. That
/// exception is first-party only, and the test below pins it shut for submissions.
/// These run against the embedded resource, so a regression can't reach production unnoticed.
/// </summary>
public class RawTemplateContractTests
{
    private static readonly string[] Slugs = ["aurora-vows", "a-love-story", "gilded-hour"];

    private static string Html(string slug)
    {
        var asm = typeof(RawTemplatePackager).Assembly;
        // Resource names mangle characters that aren't valid in an identifier (a hyphen becomes an
        // underscore), so match on the meta.json sibling's prefix the way the seeder itself does.
        var name = asm.GetManifestResourceNames()
            .Single(n => n.Contains(".RawTemplates.", StringComparison.Ordinal)
                         && n.EndsWith(".index.html", StringComparison.Ordinal)
                         && n.Replace('_', '-').Contains($".{slug}.", StringComparison.OrdinalIgnoreCase));
        using var stream = asm.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static RawTemplatePackager Packager() => new(Substitute.For<IStorageService>());

    [Theory]
    [InlineData("aurora-vows")]
    [InlineData("a-love-story")]
    [InlineData("gilded-hour")]
    public void Passes_the_scan_a_first_party_template_must_pass(string slug) =>
        RawTemplatePackager.EnsureSelfContainedAndSafe(Html(slug));

    /// <summary>
    /// Scripts are allowed now, wherever they come from. The scan is no longer the thing standing
    /// between a stranger's JavaScript and a reader — a human review is, and the frame it runs in has
    /// an opaque origin. What the scan still refuses is anything the file does not contain, because
    /// that is what makes the review mean something.
    /// </summary>
    [Fact]
    public void A_submission_may_carry_its_own_script()
    {
        var html = "<!doctype html><html><head><style>body{color:#000}</style></head>"
                 + "<body><h1 data-var=\"event.title\">T</h1><script>document.title='x'</script></body></html>";

        RawTemplatePackager.EnsureSelfContainedAndSafe(html);
    }

    [Fact]
    public void A_submission_may_not_pull_its_script_from_somewhere_else()
    {
        var html = "<!doctype html><html><head><style>body{color:#000}</style></head>"
                 + "<body><script src=\"https://cdn.example/x.js\"></script></body></html>";

        var ex = Assert.Throws<InvitesBlog.Application.Exceptions.BusinessRuleException>(
            () => RawTemplatePackager.EnsureSelfContainedAndSafe(html));

        Assert.Equal("template_external_script_not_allowed", ex.ErrorCode);
    }

    /// <summary>
    /// Every invitation must offer a way into its event's photo box (§5). The server appends a plain
    /// bar when a template has no link of its own — which exists for campaigns pinned to packages
    /// that predate the element, not as a licence for new templates to skip it. A designer placing it
    /// themselves is what makes it fit the design instead of being bolted to the bottom.
    /// </summary>
    [Theory]
    [InlineData("aurora-vows")]
    [InlineData("a-love-story")]
    [InlineData("gilded-hour")]
    public void Offers_a_way_into_the_event_photo_box(string slug) =>
        Assert.Contains("data-href=\"photos.link\"", Html(slug), StringComparison.Ordinal);

    [Theory]
    [InlineData("aurora-vows")]
    [InlineData("a-love-story")]
    [InlineData("gilded-hour")]
    public void Declares_the_three_required_theme_colours(string slug)
    {
        var manifest = Packager().BuildManifest(slug, "1.0.0", Html(slug));

        Assert.NotNull(manifest.Theme.AccentColor);
        Assert.NotNull(manifest.Theme.BackgroundColor);
        Assert.NotNull(manifest.Theme.TextColor);
    }

    [Fact]
    public void A_love_story_declares_its_roles_wardrobe_blocks_and_gallery()
    {
        var manifest = Packager().BuildManifest("a-love-story", "1.1.0", Html("a-love-story"));

        Assert.Equal(new[] { "everyone", "bridesmaids", "groomsmen" }, manifest.Roles);
        Assert.Contains("wardrobeBridesmaids", manifest.ContentBlocks);
        Assert.Contains("wardrobeGroomsmen", manifest.ContentBlocks);

        var strip = Assert.Single(manifest.ImageSlots, s => s.Key == "event.filmStrip");
        Assert.True(strip.Multiple);
        Assert.Equal(2, strip.MinImages);
        Assert.Equal(6, strip.MaxImages);

        // The dress code became a real dropdown rather than a free-text box.
        var dressCode = Assert.Single(manifest.Fields, f => f.Key == "event.dressCode");
        Assert.Equal("select", dressCode.Type);
        Assert.Contains("Black tie", dressCode.Options!);

        // Twelve wardrobe swatches, now themable instead of painted by author JavaScript.
        Assert.Equal(12, manifest.Theme.Keys.Count(k => k.Key.StartsWith("dress")));
        Assert.DoesNotContain(manifest.Fields, f => f.Key.StartsWith("event.dressEveryone"));
    }

    [Fact]
    public void Gilded_hour_declares_its_scenes_gallery_and_typed_when_where()
    {
        var manifest = Packager().BuildManifest("gilded-hour", "1.0.0", Html("gilded-hour"));

        // Three full-bleed scenes plus the fanned spread the pinned section shuffles through.
        Assert.Equal(3, manifest.ImageSlots.Count(s => s.Key.StartsWith("event.scenePhoto")));
        var gallery = Assert.Single(manifest.ImageSlots, s => s.Key == "event.gallery");
        Assert.True(gallery.Multiple);
        Assert.Equal(2, gallery.MinImages);
        Assert.Equal(6, gallery.MaxImages);

        // The caption that types out over the spread reads these two, so they must stay typed
        // (a free-text date would defeat the editor's picker).
        Assert.Equal("date", Assert.Single(manifest.Fields, f => f.Key == "event.date").Type);
        Assert.Equal("time", Assert.Single(manifest.Fields, f => f.Key == "event.time").Type);

        var dressCode = Assert.Single(manifest.Fields, f => f.Key == "event.dressCode");
        Assert.Equal("select", dressCode.Type);
        Assert.Contains("Cocktail Chic", dressCode.Options!);
    }

    [Theory]
    [InlineData("aurora-vows")]
    [InlineData("a-love-story")]
    [InlineData("gilded-hour")]
    public void Stays_within_the_hard_size_ceiling(string slug)
    {
        var bytes = Encoding.UTF8.GetByteCount(Html(slug));

        Assert.True(bytes <= RawTemplatePackager.MaxTemplateBytes,
            $"{slug} is {bytes / 1024}KB, over the {RawTemplatePackager.MaxTemplateBytes / 1024}KB ceiling.");
    }
}
