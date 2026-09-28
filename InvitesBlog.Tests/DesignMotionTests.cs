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
        scene.Stage = false; // these test the scrolling page; the stage has its own tests below
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
            Keyframes = [new() { T = 0, Opacity = 0, Y = 320 }, new() { T = 1, Opacity = 1, Y = 300 }],
            Text = new DesignText
            {
                Runs = [new() { Text = "Dear " }, new() { Var = "guest.name" }, new() { Text = " hi" }],
                Split = new DesignSplit { By = "letter", Stagger = 0.5 },
            },
        };
        var html = DesignCompiler.Compile(Scene(el));
        // D, e, a, r, the name, h, i: 7 pieces over half the track, each playing the other half.
        Assert.Equal(7, System.Text.RegularExpressions.Regex.Matches(html, "class=\"p\"").Count);
        Assert.Contains("<span class=\"p\" style=\"--i:4\" data-ts=\"233.333\" data-te=\"433.333\" data-sg=\"233.333 433.333\"><span data-var=\"guest.name\"", html);
        Assert.Contains("<span class=\"w\"><span class=\"p\" style=\"--i:0\"", html);
        Assert.Contains(".e0 .p{display:inline-block;animation:k0_0 1s linear both;animation-timeline:scroll(root);animation-range:calc((100 + var(--i) * 33.333) * var(--u)) calc((300 + var(--i) * 33.333) * var(--u))}", html);
        Assert.Matches(@"@keyframes k0_0\{0%\{transform:translate[^;]+;opacity:0;\}100%\{transform:none;opacity:1;\}\}", html);
        // The block itself doesn't move; its pieces do.
        Assert.DoesNotContain(".e0>.a{animation", html);
    }

    [Fact]
    public void Split_text_with_one_piece_plays_its_keyframes_over_the_whole_bar()
    {
        // A guest's name split by word is one piece: squeezing its keyframes into (1 - stagger) of the bar
        // made a fade out at 85-100% happen at 34-40%, nowhere near its diamonds on the timeline.
        var el = new DesignElement
        {
            Id = "t", Type = "text", X = 45, Y = 392, W = 300, H = 60,
            Track = new DesignTrack { Start = 1171, End = 1941 },
            Keyframes = [new() { T = 0.122, Opacity = 0.4 }, new() { T = 0.452, Opacity = 1 }, new() { T = 0.85, Opacity = 1 }, new() { T = 1, Opacity = 0 }],
            Text = new DesignText { Runs = [new() { Var = "guest.name" }, new() { Text = " " }], Split = new DesignSplit { By = "word", Stagger = 0.6 } },
        };
        var html = DesignCompiler.Compile(Scene(el));
        // Two changes: the way in between its keyframes (1264.94 → 1519.04), the way out between its (1825.5 → 1941).
        Assert.Contains("animation:k0_0 1s linear both,k0_1 1s linear forwards;animation-timeline:scroll(root),scroll(root);animation-range:"
            + "calc((1264.94 + var(--i) * 0) * var(--u)) calc((1519.04 + var(--i) * 0) * var(--u)),calc((1825.5 + var(--i) * 0) * var(--u)) calc((1941 + var(--i) * 0) * var(--u))", html);
        Assert.Contains("data-ts=\"1264.94\" data-te=\"1941\" data-sg=\"1264.94 1519.04 1825.5 1941\"", html);        // And Check says splitting it does nothing: a name and a space are still one piece.
        Assert.Contains(Check(Scene(el)), i => i.Code == "split_bound");
    }

    [Fact]
    public void Split_text_spreads_each_change_between_its_own_keyframes()
    {
        // In over 0-10% of the bar, out over 90-100%, three words, spread 0.5: in the way in the first
        // word starts at 0 and the last lands at 100; in the way out the first leaves at 900, the last
        // is gone at 1000. Between, every word holds.
        var el = new DesignElement
        {
            Id = "t", Type = "text", X = 20, Y = 300, W = 300, H = 60,
            Track = new DesignTrack { Start = 0, End = 1000 },
            Keyframes = [new() { T = 0, Opacity = 0 }, new() { T = 0.1, Opacity = 1 }, new() { T = 0.9, Opacity = 1 }, new() { T = 1, Opacity = 0 }],
            Text = new DesignText { Runs = [new() { Text = "Save the date" }], Split = new DesignSplit { By = "word", Stagger = 0.5 } },
        };
        var html = DesignCompiler.Compile(Scene(el));
        Assert.Contains("data-sg=\"0 50 900 950\">Save", html);
        Assert.Contains("data-sg=\"50 100 950 1000\">date", html);
        Assert.Contains("animation-range:calc((0 + var(--i) * 25) * var(--u)) calc((50 + var(--i) * 25) * var(--u)),calc((900 + var(--i) * 25) * var(--u)) calc((950 + var(--i) * 25) * var(--u))", html);
        Assert.Contains("@keyframes k0_0{0%{transform:none;opacity:0;}100%{transform:none;opacity:1;}}", html);
        Assert.Contains("@keyframes k0_1{0%{transform:none;opacity:1;}100%{transform:none;opacity:0;}}", html);
        Assert.DoesNotContain("@keyframes k0{", html);
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

    // ----- Stage ---------------------------------------------------------------------------------

    private static DesignScene Stage(params DesignElement[] elements)
    {
        var scene = Scene(elements);
        scene.Stage = true;
        return scene;
    }

    [Fact]
    public void New_designs_start_on_a_stage_and_old_ones_keep_scrolling()
    {
        Assert.True(DesignStarters.Create("blank")!.Stage);
        Assert.False(DesignStarters.Create("wedding")!.Stage);
        Assert.False(DesignScene.Parse("""{"schema":3,"elements":[]}""").Stage);
        Assert.DoesNotContain("ib-stage", DesignCompiler.Compile(DesignStarters.Create("wedding")!));
    }

    [Fact]
    public void On_a_stage_everything_sits_in_a_fixed_layer_and_only_motion_lengthens_the_page()
    {
        var still = Box("still");
        still.Y = 3000; // on a stage its y is on the screen: it doesn't make the page scroll
        var moving = Box("moving");
        moving.Track = new DesignTrack { Start = 0, End = 2000 };
        moving.Keyframes = [new() { T = 0, Opacity = 0 }, new() { T = 1, Opacity = 1 }];
        var scene = Stage(still, moving);
        scene.Elements.RemoveAll(e => e.Id == "rsvp");
        Assert.Equal(2000, scene.ScrollRange());
        var html = DesignCompiler.Compile(scene);
        Assert.Contains("<main class=\"ib-page\"><div class=\"ib-stage\">", html);
        Assert.Contains(".ib-stage{position:fixed;top:0;bottom:0;left:50%;width:calc(390 * var(--u));margin-left:calc(-195 * var(--u));overflow:hidden}", html);
        Assert.Contains(Check(scene), i => i.Code == "below_screen" && i.ElementId == "still");
    }

    [Fact]
    public void On_a_stage_a_bar_is_a_clip_shown_only_while_it_is_scrolled_through()
    {
        var el = Box();
        el.Track = new DesignTrack { Start = 200, End = 600 };
        var html = DesignCompiler.Compile(Stage(el));
        Assert.Contains("opacity:0;pointer-events:none;animation:ib-v 1s linear none;animation-timeline:scroll(root);animation-range:calc(200 * var(--u)) calc(600 * var(--u));", html);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(html, "@keyframes ib-v"));

        // Without a bar of its own it's there the whole time.
        var always = Box("always");
        always.Track = null;
        Assert.DoesNotContain("@keyframes ib-v", DesignCompiler.Compile(Stage(always)));
    }

    [Fact]
    public void On_a_stage_what_scrolls_slides_up_as_far_as_the_page_goes_and_pins_mean_nothing()
    {
        var scroller = Box("scroller");
        scroller.Track = null;
        scroller.Scrolls = true;
        scroller.Y = 1500;
        var pinned = Box("pinned");
        pinned.Pinned = true;
        pinned.Track = new DesignTrack { Start = 0, End = 300 };
        var scene = Stage(scroller, pinned);
        var range = scene.ScrollRange();
        Assert.Equal(1500 + 100 - 844, range);
        var html = DesignCompiler.Compile(scene);
        Assert.Contains($"data-sr=\"{range}\"", html);
        Assert.Contains($"@keyframes s0{{from{{transform:translateY(0px)}}to{{transform:translateY(calc(-{range} * var(--u)))}}}}", html);
        Assert.DoesNotContain("@keyframes p", html);
        // The old-browser driver steps the slide by scroll and hides clips outside their bars.
        Assert.Contains("m==='ib-v'", html);
        Assert.Contains("data-sr", html);
    }

    [Fact]
    public void A_chosen_length_makes_the_page_scroll_further_but_never_cuts_its_content_short()
    {
        var moving = Box();
        moving.Track = new DesignTrack { Start = 0, End = 1200 };
        moving.Keyframes = [new() { T = 0 }, new() { T = 1, X = 200 }];
        var scene = Stage(moving);
        scene.Elements.RemoveAll(e => e.Id == "rsvp");
        Assert.Equal(1200, scene.ScrollRange());
        scene.Length = 3000;
        Assert.Equal(3000, scene.ScrollRange());
        Assert.Contains($"height:calc({3000 + 844} * var(--u))", DesignCompiler.Compile(scene));
        scene.Length = 500;
        Assert.Equal(1200, scene.ScrollRange());
    }
}
