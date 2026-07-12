using InvitesBlog.Application.Abstractions;
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
    public void Renderer_fills_all_elements_sharing_a_path()
    {
        // The single deduped value must reach EVERY element carrying the path — the injector selects all
        // matches (querySelectorAll), not just the first (querySelector).
        Assert.Contains("querySelectorAll('[data-var]')", TemplateInjector.Js);
        Assert.Contains("querySelectorAll('[data-href]')", TemplateInjector.Js);
        Assert.Contains("querySelectorAll('[data-src]')", TemplateInjector.Js);
        Assert.DoesNotContain("querySelector('[data-var]')", TemplateInjector.Js);
    }
}
