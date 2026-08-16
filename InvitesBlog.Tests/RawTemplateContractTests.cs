using System.Reflection;
using System.Text;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Infrastructure.Templates;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// The committed raw templates are the platform's own work, so they must satisfy the rules we hold
/// community designers to — no JavaScript, within budget, and declaring a real theming surface.
/// These run against the embedded resource, so a regression can't reach production unnoticed.
/// </summary>
public class RawTemplateContractTests
{
    private static readonly string[] Slugs = ["aurora-vows", "a-love-story"];

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
    public void Passes_the_same_scan_a_community_submission_must_pass(string slug) =>
        RawTemplatePackager.EnsureSelfContainedAndSafe(Html(slug));

    [Theory]
    [InlineData("aurora-vows")]
    [InlineData("a-love-story")]
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

    [Theory]
    [InlineData("aurora-vows")]
    [InlineData("a-love-story")]
    public void Stays_within_the_hard_size_ceiling(string slug)
    {
        var bytes = Encoding.UTF8.GetByteCount(Html(slug));

        Assert.True(bytes <= RawTemplatePackager.MaxTemplateBytes,
            $"{slug} is {bytes / 1024}KB, over the {RawTemplatePackager.MaxTemplateBytes / 1024}KB ceiling.");
    }
}
