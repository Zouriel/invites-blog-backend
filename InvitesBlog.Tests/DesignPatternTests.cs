using System.Text;
using System.Text.RegularExpressions;
using InvitesBlog.TemplateCompiler.Design;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Patterns over elements: a filter per pattern, laid on the layer that moves with the element, clipped
/// to what it paints, applied as shade, shine, a second colour, emboss or engrave.
/// </summary>
public class DesignPatternTests
{
    private static DesignScene Scene(params DesignElement[] elements)
    {
        var scene = DesignStarters.Create("blank")!;
        scene.Stage = false;
        scene.Elements.Clear();
        scene.Elements.AddRange(elements);
        return scene;
    }

    private static DesignElement Box(string id, DesignPattern? pattern, double w = 120, double h = 80) => new()
    {
        Id = id, Type = "shape", X = 20, Y = 40, W = w, H = h, Pattern = pattern,
        Shape = new DesignShape { Kind = "rect", Fill = "#336699" },
    };

    private static string Filter(string html, int index) => Regex.Match(html, $"<filter id=\"f{index}\"[^>]*>.*?</filter>").Value;
    /// <summary>Every rule for an element's moving layer (text writes its layout in one and its look in another).</summary>
    private static string ARule(string html, int n) => string.Concat(Regex.Matches(html, $@"\.e{n}>\.a\{{[^}}]*\}}").Select(m => m.Value));

    [Fact]
    public void A_pattern_is_a_filter_on_the_moving_layer_after_the_page()
    {
        var html = DesignCompiler.Compile(Scene(Box("a", new DesignPattern { Kind = "clouds" })));
        Assert.Contains("filter:url(#f0);", ARule(html, 0));
        var filter = Filter(html, 0);
        Assert.Contains("<feTurbulence type=\"fractalNoise\"", filter);
        // Shade by default: black marks at half strength, kept inside what it paints.
        Assert.Contains("<feFlood style=\"flood-color:#000000\" flood-opacity=\"0.5\"/>", filter);
        Assert.Contains("operator=\"atop\"", filter);
        // The defs come after the page, in a hidden SVG that isn't display:none (filters there don't draw).
        Assert.True(html.IndexOf("</main><svg width=\"0\" height=\"0\" style=\"position:absolute\" aria-hidden=\"true\"><filter id=\"f0\"", StringComparison.Ordinal) > 0);
    }

    [Fact]
    public void No_pattern_no_filter()
    {
        var html = DesignCompiler.Compile(Scene(Box("a", null)));
        Assert.DoesNotContain("<filter", html);
        Assert.DoesNotContain("url(#f", html);
    }

    [Theory]
    [InlineData("plaid")]
    [InlineData(null)]
    public void An_unknown_pattern_draws_nothing_and_fails_validation(string? kind)
    {
        var scene = Scene(Box("a", new DesignPattern { Kind = kind }));
        Assert.DoesNotContain("<filter", DesignCompiler.Compile(scene));
        Assert.Contains(DesignValidator.Validate(scene), i => i.Code == "pattern" && i.Severity == "error");
    }

    [Fact]
    public void Strength_zero_draws_nothing()
    {
        Assert.DoesNotContain("<filter", DesignCompiler.Compile(Scene(Box("a", new DesignPattern { Kind = "dots", Strength = 0 }))));
    }

    [Fact]
    public void Shine_and_colour_lay_their_own_colours_and_a_theme_colour_stays_a_variable()
    {
        var html = DesignCompiler.Compile(Scene(
            Box("a", new DesignPattern { Kind = "dots", Mode = "shine", Strength = 0.8 }),
            Box("b", new DesignPattern { Kind = "dots", Mode = "colour", Color = "theme:accent" }),
            Box("c", new DesignPattern { Kind = "dots", Mode = "colour", Color = "#C0A060" })));
        Assert.Contains("flood-color:#ffffff\" flood-opacity=\"0.8\"", Filter(html, 0));
        Assert.Contains("flood-color:var(--ib-accent)", Filter(html, 1));
        Assert.Contains("flood-color:#c0a060", Filter(html, 2));
    }

    [Theory]
    [InlineData("emboss", "3")]
    [InlineData("engrave", "-3")]
    public void Emboss_and_engrave_light_the_marks_and_keep_to_the_outline(string mode, string depth)
    {
        var filter = Filter(DesignCompiler.Compile(Scene(Box("a", new DesignPattern { Kind = "stars", Mode = mode, Strength = 0.6 }))), 0);
        Assert.Contains($"<feDiffuseLighting in=\"m\" surfaceScale=\"{depth}\"", filter);
        Assert.Contains("mode=\"hard-light\"", filter);
        Assert.Contains("k2=\"0.6\" k3=\"0.4\"", filter);
        Assert.EndsWith("<feComposite in2=\"SourceAlpha\" operator=\"in\"/></filter>", filter);
    }

    [Fact]
    public void A_geometric_pattern_is_a_tiled_svg_over_more_than_the_box_and_scales_with_size()
    {
        var filter = Filter(DesignCompiler.Compile(Scene(Box("a", new DesignPattern { Kind = "dots", Size = 2 }, 100, 40))), 0);
        var m = Regex.Match(filter, "href=\"data:image/svg\\+xml;base64,([^\"]+)\" x=\"-25\" y=\"-10\" width=\"175\" height=\"70\"");
        Assert.True(m.Success, filter);
        var svg = Encoding.UTF8.GetString(Convert.FromBase64String(m.Groups[1].Value));
        // A 24-unit tile at size 1: 48 at size 2.
        Assert.Contains("<pattern id=\"p\" width=\"48\" height=\"48\" patternUnits=\"userSpaceOnUse\">", svg);
        Assert.Contains("<circle cx=\"12\" cy=\"12\" r=\"4.8\"/>", svg);
    }

    [Fact]
    public void Texture_frequencies_keep_their_precision_at_any_size()
    {
        var small = Filter(DesignCompiler.Compile(Scene(Box("a", new DesignPattern { Kind = "marble", Size = 3 }))), 0);
        Assert.Contains("baseFrequency=\"3.333e-3 10e-3\"", small);
        // Out-of-range sizes are held to 0.25-4.
        var huge = Filter(DesignCompiler.Compile(Scene(Box("a", new DesignPattern { Kind = "clouds", Size = 90 }))), 0);
        Assert.Contains("baseFrequency=\"2.25e-3\"", huge);
    }

    [Fact]
    public void The_same_filter_is_shared_and_different_ones_get_their_own()
    {
        var html = DesignCompiler.Compile(Scene(
            Box("a", new DesignPattern { Kind = "paper" }),
            Box("b", new DesignPattern { Kind = "paper" }, 300, 300), // a texture doesn't depend on the box
            Box("c", new DesignPattern { Kind = "paper", Mode = "shine" })));
        Assert.Contains("filter:url(#f0);", ARule(html, 0));
        Assert.Contains("filter:url(#f0);", ARule(html, 1));
        Assert.Contains("filter:url(#f1);", ARule(html, 2));
        Assert.Equal(2, Regex.Matches(html, "<filter id=").Count);
    }

    [Fact]
    public void A_looping_element_wears_its_pattern_on_the_loop_layer_so_it_turns_with_it()
    {
        var el = Box("a", new DesignPattern { Kind = "hearts" });
        el.Track = new DesignTrack { Start = 0, End = 800 };
        el.Loop = new DesignLoop { Repeat = 2, Frames = [new() { T = 0 }, new() { T = 1, Rotate = 30 }] };
        var html = DesignCompiler.Compile(Scene(el));
        Assert.Contains("filter:url(#f0);", Regex.Match(html, @"\.e0>\.a>\.l\{[^}]*\}").Value);
        Assert.DoesNotContain("filter:", ARule(html, 0));
    }

    [Fact]
    public void A_blurring_element_keeps_its_pattern_in_every_keyframe()
    {
        var el = Box("a", new DesignPattern { Kind = "linen" });
        el.Track = new DesignTrack { Start = 0, End = 600 };
        el.Keyframes = [new() { T = 0, Blur = 8 }, new() { T = 1, Blur = 0 }];
        var html = DesignCompiler.Compile(Scene(el));
        var keyframes = Regex.Match(html, @"@keyframes k0\{.*?\}\}").Value;
        Assert.Equal(2, Regex.Matches(keyframes, @"filter:url\(#f0\) blur\(").Count);
    }

    [Fact]
    public void Split_text_wears_the_pattern_once_on_the_whole_not_on_each_piece()
    {
        var el = new DesignElement
        {
            Id = "t", Type = "text", X = 20, Y = 40, W = 300, H = 60, Track = new DesignTrack { Start = 0, End = 600 },
            Keyframes = [new() { T = 0, Blur = 4 }, new() { T = 1, Blur = 0 }],
            Text = new DesignText { Runs = [new() { Text = "Two words" }], Split = new DesignSplit { By = "word" } },
            Pattern = new DesignPattern { Kind = "grid" },
        };
        var html = DesignCompiler.Compile(Scene(el));
        Assert.Contains("filter:url(#f0);", ARule(html, 0));
        Assert.Single(Regex.Matches(html, @"url\(#f0\)"));
    }

    [Fact]
    public void Validation_checks_the_way_it_applies_and_the_colour()
    {
        var bad = DesignValidator.Validate(Scene(Box("a", new DesignPattern { Kind = "dots", Mode = "glow" })));
        Assert.Contains(bad, i => i.Code == "pattern");
        var colour = DesignValidator.Validate(Scene(Box("a", new DesignPattern { Kind = "dots", Mode = "colour", Color = "url(x)" })));
        Assert.Contains(colour, i => i.Code == "color" && i.Severity == "error");
        var fine = DesignValidator.Validate(Scene(Box("a", new DesignPattern { Kind = "dots", Mode = "colour", Color = "theme:accent" })));
        Assert.DoesNotContain(fine, i => i.Code is "pattern" or "color");
    }

    [Fact]
    public void Every_kind_and_way_compiles()
    {
        foreach (var kind in DesignPatterns.Kinds)
            foreach (var mode in DesignPatterns.Modes)
            {
                var html = DesignCompiler.Compile(Scene(Box("a", new DesignPattern { Kind = kind, Mode = mode, Color = "#123456" })));
                Assert.Contains("filter:url(#f0);", ARule(html, 0));
                Assert.Matches("result=\"m\"", Filter(html, 0));
            }
    }
}
