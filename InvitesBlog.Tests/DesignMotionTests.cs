using InvitesBlog.Infrastructure.Templates;
using InvitesBlog.TemplateCompiler.Design;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>The motion extras of 2026-09-28: loops, pivots, 3D, skew, blur, clips, draw-on, tracking, split text, tap to scroll.</summary>
public class DesignMotionTests
{
    private static DesignScene Scene(params DesignElement[] elements)
    {
        var scene = DesignStarters.Create("blank")!;
        scene.Elements.AddRange(elements);
        scene.Elements.Add(new DesignElement
        {
            Id = "rsvp", Type = "rsvp", X = 95, Y = 1968, W = 200, H = 52,
            Button = new DesignButton { Fill = "theme:accent", Style = new DesignTypography { Color = "theme:bg" } },
        });
        return scene;
    }

    private static DesignElement Box(string id = "b") => new()
    {
        Id = id, Type = "shape", X = 20, Y = 300, W = 100, H = 100,
        Track = new DesignTrack { Start = 0, End = 800 },
        Shape = new DesignShape { Kind = "rect", Fill = "theme:accent" },
    };

    private static IReadOnlyList<DesignIssue> Check(DesignScene scene) => DesignValidator.Validate(scene);

    [Fact]
    public void Designs_that_use_none_of_it_compile_without_any_of_it()
    {
        var html = DesignCompiler.Compile(DesignStarters.Create("wedding")!);
        foreach (var token in new[] { "class=\"l\"", "@property", "perspective:", "filter:", "clip-path", "pathLength", "data-scroll-to", "class=\"p\"", "backface" })
            Assert.DoesNotContain(token, html);
        // The tap script only comes with an element that uses it.
        Assert.DoesNotContain("data-scroll-to]'", html);
    }

    [Fact]
    public void A_loop_plays_on_its_own_layer_repeated_across_the_track()
    {
        var el = Box();
        el.Keyframes = [new() { T = 0, Opacity = 0 }, new() { T = 0.15, Opacity = 1 }];
        el.Loop = new DesignLoop { Repeat = 4, Alternate = true, Frames = [new() { T = 0, Rotate = -4 }, new() { T = 1, Rotate = 4 }] };
        var html = DesignCompiler.Compile(Scene(el));

        Assert.Contains("<div class=\"a\"><div class=\"l\"><svg", html);
        Assert.Contains("animation:l0 1s linear both;animation-iteration-count:4;animation-direction:alternate;", html);
        Assert.Contains("@keyframes l0{0%{transform:rotate(-4deg);opacity:1;}100%{transform:rotate(4deg);opacity:1;}}", html);
        // The entrance still plays on .a, underneath.
        Assert.Contains("animation:k0 1s linear both", html);
        Assert.DoesNotContain(Check(Scene(el)), i => i.Severity == DesignIssue.Error);
    }

    [Fact]
    public void A_loop_alone_still_gives_the_old_browser_driver_its_track()
    {
        var el = Box();
        el.Loop = new DesignLoop { Repeat = 2, Frames = [new() { T = 0.5, Dy = -10 }] };
        var html = DesignCompiler.Compile(Scene(el));
        Assert.Contains("data-ts=\"0\" data-te=\"800\"", html);
        // …and that driver steps loops through every repeat, not just the first.
        Assert.Contains("activeDuration", html);
        Assert.Contains("className==='l'", html);
    }

    [Fact]
    public void A_pivot_moves_the_transform_origin_of_both_layers()
    {
        var el = Box();
        el.Origin = new DesignOrigin { X = 0.5, Y = 0 };
        el.Loop = new DesignLoop { Frames = [new() { T = 1, Rotate = 10 }] };
        var html = DesignCompiler.Compile(Scene(el));
        Assert.Contains(".e0>.a{transform-origin:50% 0%;}", html);
        Assert.Contains(".e0>.a>.l{position:relative;width:100%;height:100%;transform-origin:50% 0%;", html);
    }

    [Fact]
    public void Three_d_turns_get_perspective_and_a_hidden_back()
    {
        var el = Box();
        el.BackfaceHidden = true;
        el.Keyframes = [new() { T = 0, RotateX = 90 }, new() { T = 1, RotateX = 0 }];
        var html = DesignCompiler.Compile(Scene(el));
        Assert.Contains("perspective:calc(600 * var(--u))", html);
        Assert.Contains("transform:rotateX(90deg);", html);
        Assert.Contains("backface-visibility:hidden", html);
    }

    [Fact]
    public void Blur_and_skew_are_clamped_and_written_on_every_keyframe()
    {
        var el = Box();
        el.Keyframes = [new() { T = 0, Blur = 90, SkewX = -200 }, new() { T = 1, Blur = 0, SkewX = 0 }];
        var html = DesignCompiler.Compile(Scene(el));
        Assert.Contains("skewX(-80deg)", html);
        Assert.Contains("filter:blur(calc(40 * var(--u)))", html);
        Assert.Contains("filter:blur(0px)", html);
    }

    [Fact]
    public void Clips_need_a_shape_and_start_uncut()
    {
        var el = Box();
        el.ClipShape = "inset";
        el.Keyframes = [new() { T = 0, Clip = [0, 100, 0, 0] }, new() { T = 1 }];
        var html = DesignCompiler.Compile(Scene(el));
        Assert.Contains("clip-path:inset(0% 100% 0% 0%)", html);
        Assert.Contains("clip-path:inset(0% 100% 0% 0%);", html); // carried forward to the held last frame

        var circle = Box();
        circle.ClipShape = "circle";
        circle.Keyframes = [new() { T = 0.5, Clip = [0] }, new() { T = 1, Clip = [500] }];
        var html2 = DesignCompiler.Compile(Scene(circle));
        Assert.Contains("circle(0% at 50% 50%)", html2);
        Assert.Contains("circle(150% at 50% 50%)", html2);

        var loose = Box();
        loose.Keyframes = [new() { T = 0, Clip = [50] }];
        Assert.Contains(Check(Scene(loose)), i => i.Code == "clip_unused");
        Assert.DoesNotContain("clip-path", DesignCompiler.Compile(Scene(loose)));
    }

    [Fact]
    public void Draw_on_dashes_the_outline_and_registers_its_property_once()
    {
        var a = Box("a");
        a.Shape!.Stroke = "theme:accent";
        a.Shape.StrokeWidth = 2;
        a.Keyframes = [new() { T = 0, Draw = 0 }, new() { T = 1, Draw = 1 }];
        var b = Box("b2");
        b.Shape = new DesignShape { Kind = "line", Stroke = "theme:text", StrokeWidth = 2 };
        b.Keyframes = [new() { T = 0, Draw = 0 }, new() { T = 1, Draw = 1 }];
        var html = DesignCompiler.Compile(Scene(a, b));
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "@property --d"));
        Assert.Contains("<rect pathLength=\"1\"", html);
        Assert.Contains("<line pathLength=\"1\"", html);
        Assert.Contains("stroke-dashoffset:calc(1 - var(--d))", html);
        Assert.Contains("0%{transform:none;opacity:1;--d:0;}", html);

        var filled = Box("c");
        filled.Keyframes = [new() { T = 0, Draw = 0 }];
        Assert.Contains(Check(Scene(filled)), i => i.Code == "draw_needs_outline");
    }

    [Fact]
    public void Split_text_animates_each_piece_with_its_own_range_and_keeps_fields_whole()
    {
        var el = new DesignElement
        {
            Id = "t", Type = "text", X = 20, Y = 300, W = 300, H = 60,
            Track = new DesignTrack { Start = 100, End = 500 },
            Keyframes = [new() { T = 0, Opacity = 0, Y = 320 }, new() { T = 1 }],
            Text = new DesignText
            {
                Runs = [new() { Text = "Dear " }, new() { Var = "guest.name" }, new() { Text = " hi" }],
                Split = new DesignSplit { By = "letter", Stagger = 0.5 },
            },
        };
        var html = DesignCompiler.Compile(Scene(el));
        // D, e, a, r, the name, h, i: 7 pieces over half the track, each playing the other half.
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(html, "class=\"p\"").Count);
        Assert.Contains("<span class=\"p\" style=\"--i:4\" data-ts=\"233.333\" data-te=\"433.333\"><span data-var=\"guest.name\"", html);
        Assert.Contains("<span class=\"w\"><span class=\"p\" style=\"--i:0\"", html);
        Assert.Contains(".e0 .p{display:inline-block;animation:k0 1s linear both;animation-timeline:scroll(root);animation-range:calc((100 + var(--i) * 33.333) * var(--u)) calc((300 + var(--i) * 33.333) * var(--u))}", html);
        // The block itself doesn't move; its pieces do.
        Assert.DoesNotContain(".e0>.a{animation", html);
    }

    [Fact]
    public void Split_text_that_is_all_fields_is_worth_a_warning()
    {
        var el = new DesignElement
        {
            Id = "t", Type = "text", X = 20, Y = 300, W = 300, H = 60,
            Keyframes = [new() { T = 0, Opacity = 0 }, new() { T = 1 }],
            Text = new DesignText { Runs = [new() { Var = "guest.name" }], Split = new DesignSplit { By = "word" } },
        };
        Assert.Contains(Check(Scene(el)), i => i.Code == "split_bound");
    }

    [Fact]
    public void Tracking_animates_letter_spacing_on_top_of_the_texts_own()
    {
        var el = new DesignElement
        {
            Id = "t", Type = "text", X = 20, Y = 300, W = 300, H = 60,
            Keyframes = [new() { T = 0, Tracking = 0.5 }, new() { T = 1, Tracking = 0 }],
            Text = new DesignText { Runs = [new() { Text = "Hana" }], Style = new DesignTypography { LetterSpacing = 0.1 } },
        };
        var html = DesignCompiler.Compile(Scene(el));
        Assert.Contains("@property --ls{syntax:'<length>';inherits:true;initial-value:0px}", html);
        Assert.Contains("letter-spacing:calc(0.1em + var(--ls));", html);
        Assert.Contains("--ls:0.5em;", html);
    }

    [Fact]
    public void Tap_to_scroll_writes_a_number_and_brings_the_platform_script()
    {
        var el = Box();
        el.TapScroll = 900.5;
        var html = DesignCompiler.Compile(Scene(el));
        Assert.Contains("data-scroll-to=\"900.5\" role=\"button\" tabindex=\"0\"", html);
        Assert.Contains("closest('[data-scroll-to]')", html);
        RawTemplatePackager.EnsureSelfContainedAndSafe(html);
    }

    [Fact]
    public void A_loop_too_fast_to_see_is_worth_a_warning()
    {
        var el = Box();
        el.Loop = new DesignLoop { Repeat = 40, Frames = [new() { T = 1, Rotate = 360 }] };
        Assert.Contains(Check(Scene(el)), i => i.Code == "loop_fast");
        el.Loop.Repeat = 99;
        Assert.Contains(Check(Scene(el)), i => i.Code == "loop_repeat" && i.Severity == DesignIssue.Error);
    }

    [Fact]
    public void Every_preset_is_well_formed()
    {
        foreach (var preset in DesignCatalog.EnterPresets.Concat(DesignCatalog.ExitPresets))
        {
            Assert.InRange(preset.Frames.Length, 2, DesignCatalog.MaxKeyframes);
            Assert.All(preset.Frames, f => Assert.InRange(f.T, 0, 1));
            Assert.True(preset.Frames.Select(f => f.T).SequenceEqual(preset.Frames.Select(f => f.T).Order()), preset.Id);
            if (preset.Frames.Any(f => f.Clip is not null)) Assert.NotNull(preset.ClipShape);
            if (preset.Frames.Any(f => f.Easing is not null)) Assert.All(preset.Frames.Where(f => f.Easing is not null), f => Assert.NotNull(DesignCss.Easing(f.Easing)));
        }
        Assert.Equal(DesignCatalog.EnterPresets.Count, DesignCatalog.EnterPresets.Select(p => p.Id).Distinct().Count());
        Assert.Equal(DesignCatalog.ExitPresets.Count, DesignCatalog.ExitPresets.Select(p => p.Id).Distinct().Count());
        foreach (var loop in DesignCatalog.LoopPresets)
        {
            Assert.InRange(loop.Frames.Length, 2, DesignCatalog.MaxKeyframes);
            Assert.InRange(loop.Repeat, 1, DesignCatalog.MaxLoopRepeat);
        }
    }
}
