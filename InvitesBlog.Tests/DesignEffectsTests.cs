using System.Text.RegularExpressions;
using InvitesBlog.TemplateCompiler.Design;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Layer effects: bevel, inner and outer glow in one filter with the pattern (in Photoshop's order), and a
/// blend mode on the element that mixes it with everything under it, the page colour included.
/// </summary>
public class DesignEffectsTests
{
    private static DesignScene Scene(bool stage, params DesignElement[] elements)
    {
        var scene = DesignStarters.Create("blank")!;
        scene.Stage = stage;
        scene.Elements.Clear();
        scene.Elements.AddRange(elements);
        return scene;
    }

    private static DesignElement Box(string id, double w = 120, double h = 80) => new()
    {
        Id = id, Type = "shape", X = 20, Y = 40, W = w, H = h,
        Shape = new DesignShape { Kind = "rect", Fill = "#336699" },
    };

    private static string Filter(string html, int index) => Regex.Match(html, $"<filter id=\"f{index}\"[^>]*>.*?</filter>").Value;
    private static string ARule(string html, int n) => string.Concat(Regex.Matches(html, $@"\.e{n}>\.a\{{[^}}]*\}}").Select(m => m.Value));
    private static string BoxRule(string html, int n) => Regex.Match(html, $@"\.e{n}\{{[^}}]*\}}").Value;

    [Fact]
    public void A_bevel_lights_the_blurred_outline_from_the_top_left_and_keeps_to_it()
    {
        var el = Box("a");
        el.Bevel = new DesignBevel { Size = 6, Strength = 0.8 };
        var html = DesignCompiler.Compile(Scene(false, el));
        Assert.Contains("filter:url(#f0);", ARule(html, 0));
        var f = Filter(html, 0);
        Assert.Contains("<feGaussianBlur in=\"SourceAlpha\" stdDeviation=\"3\"/><feDiffuseLighting surfaceScale=\"7.5\"", f);
        Assert.Contains("<feDistantLight azimuth=\"225\" elevation=\"45\"/>", f);
        Assert.Contains("<feBlend in2=\"SourceGraphic\" mode=\"hard-light\"/>", f);
        Assert.Contains("k2=\"0.8\" k3=\"0.2\"", f);
        Assert.EndsWith("<feComposite in2=\"SourceGraphic\" operator=\"in\" result=\"bv\"/></filter>", f);
    }

    [Fact]
    public void Pressed_in_turns_the_slope_over()
    {
        var el = Box("a");
        el.Bevel = new DesignBevel { Style = "pressed", Size = 4 };
        Assert.Contains("surfaceScale=\"-5\"", Filter(DesignCompiler.Compile(Scene(false, el)), 0));
    }

    [Fact]
    public void An_inner_glow_comes_in_from_the_rim_in_its_colour_and_blend()
    {
        var el = Box("a");
        el.InnerGlow = new DesignGlow { Color = "theme:accent", Size = 12, Strength = 0.6, Blend = "overlay" };
        var f = Filter(DesignCompiler.Compile(Scene(false, el)), 0);
        Assert.Contains("<feFuncA type=\"table\" tableValues=\"1 0\"/>", f);
        Assert.Contains("stdDeviation=\"6\"", f);
        Assert.Contains("<feFlood style=\"flood-color:var(--ib-accent)\" flood-opacity=\"0.6\"/>", f);
        Assert.Contains("<feBlend in2=\"SourceGraphic\" mode=\"overlay\" result=\"ig\"/>", f);
    }

    [Fact]
    public void An_inner_glow_is_white_and_screened_unless_told_otherwise()
    {
        var el = Box("a");
        el.InnerGlow = new DesignGlow { Blend = "sparkle", Color = "url(x)" };
        var f = Filter(DesignCompiler.Compile(Scene(false, el)), 0);
        Assert.Contains("flood-color:#ffffff", f);
        Assert.Contains("mode=\"screen\"", f);
    }

    [Fact]
    public void An_outer_glow_goes_under_the_element_with_room_around_the_box()
    {
        var el = Box("a", 100, 40);
        el.OuterGlow = new DesignGlow { Color = "#FFCC00", Size = 20 };
        var f = Filter(DesignCompiler.Compile(Scene(false, el)), 0);
        Assert.Contains("<feFlood style=\"flood-color:#ffcc00\"", f);
        Assert.EndsWith("<feMerge><feMergeNode/><feMergeNode in=\"SourceGraphic\"/></feMerge></filter>", f);
        // 30 units of reach: 30% of 100 across, 75% of 40 down.
        Assert.StartsWith("<filter id=\"f0\" x=\"-30%\" y=\"-75%\" width=\"160%\" height=\"250%\"", f);
    }

    [Fact]
    public void Pattern_inner_glow_bevel_and_outer_glow_chain_in_that_order()
    {
        var el = Box("a");
        el.Pattern = new DesignPattern { Kind = "dots" };
        el.Bevel = new DesignBevel();
        el.InnerGlow = new DesignGlow();
        el.OuterGlow = new DesignGlow();
        var f = Filter(DesignCompiler.Compile(Scene(false, el)), 0);
        int At(string s) { var i = f.IndexOf(s, StringComparison.Ordinal); Assert.True(i >= 0, s); return i; }
        Assert.True(At("operator=\"atop\" result=\"p\"/>") < At("<feBlend in2=\"p\" mode=\"screen\" result=\"ig\"/>"));
        Assert.True(At("result=\"ig\"") < At("<feBlend in2=\"ig\" mode=\"hard-light\"/>"));
        Assert.True(At("result=\"bv\"") < At("<feMergeNode in=\"bv\"/>"));
    }

    [Fact]
    public void A_pattern_alone_compiles_exactly_as_before()
    {
        var el = Box("a");
        el.Pattern = new DesignPattern { Kind = "clouds" };
        var p = DesignPatterns.Resolve(el.Pattern)!;
        Assert.Equal(DesignPatterns.Filter("f0", p, el.W, el.H, null), Filter(DesignCompiler.Compile(Scene(false, el)), 0));
    }

    [Fact]
    public void Nothing_to_draw_no_filter()
    {
        var a = Box("a");
        a.Bevel = new DesignBevel { Strength = 0 };
        a.InnerGlow = new DesignGlow { Strength = 0 };
        a.OuterGlow = new DesignGlow { Strength = -1 };
        Assert.DoesNotContain("<filter", DesignCompiler.Compile(Scene(false, a)));
    }

    [Fact]
    public void Effects_ride_the_loop_layer_and_the_blur_keyframes()
    {
        var looped = Box("a");
        looped.OuterGlow = new DesignGlow();
        looped.Track = new DesignTrack { Start = 0, End = 800 };
        looped.Loop = new DesignLoop { Repeat = 2, Frames = [new() { T = 0 }, new() { T = 1, Rotate = 30 }] };
        var blurred = Box("b");
        blurred.Bevel = new DesignBevel();
        blurred.Track = new DesignTrack { Start = 0, End = 600 };
        blurred.Keyframes = [new() { T = 0, Blur = 8 }, new() { T = 1, Blur = 0 }];
        var html = DesignCompiler.Compile(Scene(false, looped, blurred));
        Assert.Contains("filter:url(#f0);", Regex.Match(html, @"\.e0>\.a>\.l\{[^}]*\}").Value);
        Assert.Equal(2, Regex.Matches(Regex.Match(html, @"@keyframes k1\{.*?\}\}").Value, @"filter:url\(#f1\) blur\(").Count);
    }

    [Fact]
    public void A_blend_mode_mixes_the_element_and_the_page_paints_its_colour_where_it_shows()
    {
        var a = Box("a");
        a.Blend = "multiply";
        var b = Box("b");
        b.Blend = "normal";
        var html = DesignCompiler.Compile(Scene(true, a, b));
        Assert.Contains("mix-blend-mode:multiply;", BoxRule(html, 0));
        Assert.DoesNotContain("mix-blend-mode", BoxRule(html, 1));
        // The stage is a layer of its own: without its own background, a blend would see nothing under it.
        Assert.Contains(".ib-page,.ib-stage{background:var(--ib-bg)}", html);
    }

    [Fact]
    public void No_blend_no_page_background_and_unknown_blends_do_nothing()
    {
        var a = Box("a");
        a.Blend = "plus-lighter";
        var html = DesignCompiler.Compile(Scene(false, a));
        Assert.DoesNotContain("mix-blend-mode", html);
        Assert.DoesNotContain(".ib-page{background", html);
    }

    [Fact]
    public void A_group_blends_and_glows_as_one()
    {
        var child = Box("c");
        var group = new DesignElement { Id = "g", Type = "group", X = 0, Y = 0, W = 200, H = 200, Blend = "screen", OuterGlow = new DesignGlow(), Children = [child] };
        var html = DesignCompiler.Compile(Scene(false, group));
        Assert.Contains("mix-blend-mode:screen;", BoxRule(html, 0));
        Assert.Contains("filter:url(#f0);", ARule(html, 0));
        Assert.DoesNotContain("filter:", ARule(html, 1));
    }

    [Fact]
    public void Validation_checks_styles_blends_numbers_and_colours()
    {
        var a = Box("a");
        a.Bevel = new DesignBevel { Style = "pillow" };
        a.InnerGlow = new DesignGlow { Blend = "sparkle", Color = "url(x)" };
        a.OuterGlow = new DesignGlow { Size = double.NaN };
        a.Blend = "plus-lighter";
        var issues = DesignValidator.Validate(Scene(false, a));
        Assert.Contains(issues, i => i.Code == "effect" && i.Message.Contains("bevel style"));
        Assert.Contains(issues, i => i.Code == "effect" && i.Message.Contains("inner glow blend"));
        Assert.Contains(issues, i => i.Code == "effect" && i.Message.Contains("outer glow size"));
        Assert.Contains(issues, i => i.Code == "color" && i.Severity == "error");
        Assert.Contains(issues, i => i.Code == "blend" && i.Severity == "error");

        var fine = Box("b");
        fine.Bevel = new DesignBevel { Style = "pressed" };
        fine.InnerGlow = new DesignGlow { Color = "theme:accent", Blend = "soft-light" };
        fine.OuterGlow = new DesignGlow { Color = "#fff" };
        fine.Blend = "luminosity";
        Assert.DoesNotContain(DesignValidator.Validate(Scene(false, fine)), i => i.Code is "effect" or "blend" or "color");
    }

    [Fact]
    public void Every_blend_mode_compiles()
    {
        foreach (var mode in DesignEffects.BlendModes.Where(m => m != "normal"))
        {
            var a = Box("a");
            a.Blend = mode;
            a.InnerGlow = new DesignGlow { Blend = mode };
            var html = DesignCompiler.Compile(Scene(false, a));
            Assert.Contains($"mix-blend-mode:{mode};", BoxRule(html, 0));
            Assert.Contains($"mode=\"{mode}\" result=\"ig\"", Filter(html, 0));
        }
    }
}
