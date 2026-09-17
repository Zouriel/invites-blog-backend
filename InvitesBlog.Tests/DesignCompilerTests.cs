using Xunit;
using System.Text.Json.Nodes;
using AngleSharp.Html.Parser;
using InvitesBlog.Infrastructure.Templates;
using InvitesBlog.TemplateCompiler;
using InvitesBlog.TemplateCompiler.Design;
using NSubstitute;
using InvitesBlog.Application.Abstractions;

namespace InvitesBlog.Tests;

public class DesignCompilerTests
{
    public static TheoryData<string> Starters()
    {
        var data = new TheoryData<string>();
        foreach (var s in DesignStarters.All) data.Add(s.Id);
        return data;
    }

    [Theory]
    [MemberData(nameof(Starters))]
    public void Every_starter_passes_check_with_no_errors(string id)
    {
        var scene = DesignStarters.Create(id)!;
        var html = DesignCompiler.Compile(scene);
        var issues = DesignValidator.Validate(scene).Concat(DesignValidator.CheckCompiled(html)).ToList();

        Assert.DoesNotContain(issues, i => i.Severity == DesignIssue.Error);
    }

    [Theory]
    [MemberData(nameof(Starters))]
    public void Every_starter_passes_check_with_no_warnings_about_the_page(string id)
    {
        var scene = DesignStarters.Create(id)!;
        Assert.DoesNotContain(DesignValidator.Validate(scene), i => i.Code is "track_unreachable" or "off_page" or "page_too_long");
    }

    [Theory]
    [MemberData(nameof(Starters))]
    public void Every_starter_is_accepted_by_the_platform_packager(string id)
    {
        var scene = DesignStarters.Create(id)!;
        var html = DesignCompiler.Compile(scene);

        RawTemplatePackager.EnsureSelfContainedAndSafe(html);
        var manifest = new RawTemplatePackager(Substitute.For<IStorageService>()).BuildManifest("starter", "1.0.0", html);

        Assert.Contains(manifest.Fields, f => f.Key == "rsvp.link");
        Assert.Contains(manifest.Theme.Keys, k => k.CssVar == "--ib-accent" && k.Type == "color");
        Assert.Contains(manifest.Theme.Keys, k => k.CssVar == "--ib-heading-font" && k.Type == "font");
    }

    [Fact]
    public void Output_is_deterministic()
    {
        var a = DesignCompiler.Compile(DesignStarters.Create("wedding")!);
        var b = DesignCompiler.Compile(DesignScene.Parse(DesignStarters.Create("wedding")!.ToJson()));
        Assert.Equal(a, b);
    }

    [Fact]
    public void Theme_variables_are_the_first_declarations_in_the_stylesheet()
    {
        var html = DesignCompiler.Compile(DesignStarters.Create("blank")!);
        var style = html.IndexOf("<style>", StringComparison.Ordinal);
        Assert.Equal(style + "<style>".Length, html.IndexOf(":root{--ib-accent:", StringComparison.Ordinal));
    }

    [Fact]
    public void Never_emits_viewport_height_units_for_layout_or_reduced_motion_rules()
    {
        var html = DesignCompiler.Compile(DesignStarters.Create("wedding")!);
        Assert.DoesNotContain("prefers-reduced-motion", html);
        Assert.DoesNotContain("dvh", html);
        Assert.DoesNotContain("100vh", html);
        Assert.DoesNotContain("<link", html);
        Assert.DoesNotContain("http://", html);
        Assert.DoesNotContain("https://", html);
    }

    [Fact]
    public void Tracks_compile_to_length_ranges_on_the_root_scroller()
    {
        var scene = Minimal();
        var el = scene.Elements[0];
        el.Track = new DesignTrack { Start = 120, End = 400 };
        el.Keyframes = [new() { T = 0, Y = el.Y + 40, Opacity = 0, Easing = "ease-out" }, new() { T = 1, Opacity = 1 }];

        var html = DesignCompiler.Compile(scene);

        Assert.Contains("animation-timeline:scroll(root);animation-range:calc(120 * var(--u)) calc(400 * var(--u))", html);
        Assert.Contains("0%{transform:translate(0px,calc(40 * var(--u)));opacity:0;animation-timing-function:ease-out;}", html);
        Assert.Contains("100%{transform:none;opacity:1;}", html);
    }

    [Fact]
    public void Properties_a_keyframe_leaves_out_carry_over_from_the_previous_one()
    {
        var el = new DesignElement { Id = "a", Type = "text", X = 10, Y = 100 };
        el.Keyframes = [new() { T = 0.2, X = 50 }, new() { T = 0.6, Opacity = 0.5 }];

        var frames = DesignCompiler.ResolveFrames(el);

        Assert.Equal([0, 0.2, 0.6, 1], frames.Select(f => f.T));
        Assert.Equal(50, frames[0].X);   // held before the first keyframe
        Assert.Equal(50, frames[2].X);   // carried past a keyframe that didn't set it
        Assert.Equal(0.5, frames[3].Opacity);
    }

    [Fact]
    public void A_pinned_element_cancels_the_scroll_on_its_outer_box_only()
    {
        var scene = Minimal();
        var el = scene.Elements[0];
        el.Pinned = true;
        el.Track = new DesignTrack { Start = 0, End = 500 };

        var html = DesignCompiler.Compile(scene);

        Assert.Contains("to{transform:translateY(calc(500 * var(--u)))}", html);
    }

    [Fact]
    public void A_keyframe_lift_animates_z_index_on_the_box_alongside_the_pin()
    {
        var scene = Minimal();
        var el = scene.Elements[0];
        el.Pinned = true;
        el.Track = new DesignTrack { Start = 100, End = 700 };
        el.Keyframes = [new() { T = 0 }, new() { T = 0.5, Scale = 1.2, Lift = 30 }, new() { T = 1, Lift = 0 }];

        var html = DesignCompiler.Compile(scene);

        // Both animations on the outer box, each with its own timeline and range.
        Assert.Matches(@"\.e0\{[^}]*animation:p0 1s linear both,z0 1s linear both;animation-timeline:scroll\(root\),scroll\(root\)", html);
        Assert.Contains("@keyframes z0{0%{z-index:0}50%{z-index:30}100%{z-index:0}}", html);
        Assert.Equal([0, 30, 0], DesignCompiler.ResolveFrames(el).Select(f => f.Lift));
    }

    [Fact]
    public void A_photo_out_of_the_gallery_binds_its_place_and_the_host_is_asked_for_one_gallery()
    {
        var scene = Minimal();
        for (var i = 1; i <= 3; i++)
            scene.Elements.Add(new DesignElement
            {
                Id = "p" + i, Type = "slot", X = 0, Y = 0, W = 100, H = 120,
                Slot = new DesignSlot { Path = "event.gallery", Label = "Party photos", Index = i },
            });

        var html = DesignCompiler.Compile(scene);
        var manifest = new RawTemplatePackager(Substitute.For<IStorageService>()).BuildManifest("fan", "1.0.0", html);
        var gallery = Assert.Single(manifest.ImageSlots, s => s.Key.StartsWith("event.gallery", StringComparison.Ordinal));
        Assert.Equal("event.gallery", gallery.Key);
        Assert.True(gallery.Multiple);

        // Two photos uploaded: the first two prints show them, the third hides.
        var data = DesignSampleData.Build(scene, DesignSampleData.Filled);
        data["event"]!["gallery"] = new JsonArray("https://cdn.test/a.jpg", "https://cdn.test/b.jpg");
        var doc = new HtmlParser().ParseDocument(ServerBinder.Bind(html, data));
        Assert.Equal("https://cdn.test/b.jpg", doc.QuerySelector("[data-src='event.gallery.1']")!.GetAttribute("src"));
        Assert.Contains("display:none", doc.QuerySelector("[data-src='event.gallery.2']")!.Closest(".e")!.GetAttribute("style"));
    }

    [Fact]
    public void A_drawn_shape_compiles_to_path_data_in_its_own_stretchable_space_and_passes_check()
    {
        var scene = Minimal();
        scene.Elements.Add(new DesignElement
        {
            Id = "door", Type = "shape", X = 40, Y = 60, W = 120, H = 200,
            Shape = new DesignShape
            {
                Kind = "path", Fill = "theme:accent",
                Path = new DesignPath
                {
                    Width = 120, Height = 200,
                    Contours =
                    [
                        new DesignContour
                        {
                            Closed = true,
                            Points =
                            [
                                new() { X = 0, Y = 200 },
                                new() { X = 0, Y = 60, Out = new DesignXY { X = 0, Y = 27 } },
                                new() { X = 60, Y = 0, In = new DesignXY { X = 27, Y = 0 }, Out = new DesignXY { X = 93, Y = 0 } },
                                new() { X = 120, Y = 60, In = new DesignXY { X = 120, Y = 27 } },
                                new() { X = 120, Y = 200 },
                            ],
                        },
                        new DesignContour { Closed = false, Points = [new() { X = 60, Y = 60 }, new() { X = 60, Y = 200 }] },
                    ],
                },
            },
        });

        var html = DesignCompiler.Compile(scene);
        var issues = DesignValidator.Validate(scene).Concat(DesignValidator.CheckCompiled(html)).ToList();

        Assert.Contains("viewBox=\"0 0 120 200\"", html);
        Assert.Contains("d=\"M0 200L0 60C0 27 27 0 60 0C93 0 120 27 120 60L120 200L0 200Z\"", html);
        Assert.Contains("d=\"M60 60L60 200\"", html);
        Assert.DoesNotContain(issues, i => i.Severity == DesignIssue.Error);
    }

    [Fact]
    public void A_drawn_shape_with_nothing_or_too_much_in_it_fails_check()
    {
        var empty = Minimal();
        empty.Elements.Add(new DesignElement { Id = "s", Type = "shape", W = 10, H = 10, Shape = new DesignShape { Kind = "path", Path = new DesignPath() } });
        Assert.Contains(DesignValidator.Validate(empty), i => i.Code == "shape_path" && i.Severity == DesignIssue.Error);

        var huge = Minimal();
        var points = Enumerable.Range(0, DesignCatalog.MaxPathPoints + 1).Select(i => new DesignPathPoint { X = i, Y = i }).ToList();
        huge.Elements.Add(new DesignElement { Id = "s", Type = "shape", W = 10, H = 10, Shape = new DesignShape { Kind = "path", Path = new DesignPath { Contours = [new DesignContour { Points = points }] } } });
        Assert.Contains(DesignValidator.Validate(huge), i => i.Code == "shape_path" && i.Severity == DesignIssue.Error);
    }

    [Fact]
    public void Bound_elements_are_always_optional_and_carry_field_hints()
    {
        var scene = Minimal();
        scene.Fields.Add(new DesignCustomField { Path = "event.dressTone", Label = "Dress tone", Type = "select", Options = ["Formal", "Casual"] });
        scene.Elements.Add(new DesignElement
        {
            Id = "f", Type = "text", X = 0, Y = 0, W = 100, H = 40,
            Text = new DesignText { Runs = [new() { Text = "Wear: " }, new() { Var = "event.dressTone" }] },
        });

        var html = DesignCompiler.Compile(scene);
        var doc = new HtmlParser().ParseDocument(html);
        var span = doc.QuerySelector("[data-var='event.dressTone']")!;

        Assert.Equal("select", span.GetAttribute("data-type"));
        Assert.Equal("Formal,Casual", span.GetAttribute("data-options"));
        Assert.True(span.Closest(".e")!.HasAttribute("data-optional"));
    }

    [Fact]
    public void Binding_the_compiled_page_fills_values_and_hides_what_is_missing()
    {
        var scene = DesignStarters.Create("blank")!;
        var html = DesignCompiler.Compile(scene);

        var filled = new HtmlParser().ParseDocument(ServerBinder.Bind(html, DesignSampleData.Build(scene, DesignSampleData.Filled)));
        Assert.Equal("Saturday, 28 August 2027", filled.QuerySelector("[data-var='event.date']")!.TextContent);

        var empty = new HtmlParser().ParseDocument(ServerBinder.Bind(html, DesignSampleData.Build(scene, DesignSampleData.Empty)));
        var dateBox = empty.QuerySelector("[data-var='event.date']")!.Closest(".e")!;
        Assert.Contains("display:none", dateBox.GetAttribute("style"));
    }

    // ----- Page length: no screens, the page ends where its content does ------------------------------

    [Fact]
    public void The_page_ends_where_its_lowest_element_does()
    {
        var scene = Minimal();
        scene.Elements.Add(new DesignElement { Id = "low", Type = "shape", X = 0, Y = 2000, W = 100, H = 150, Shape = new DesignShape { Kind = "rect", Fill = "#000000" } });

        Assert.Equal(2150 - DesignCanvas.ReferenceViewport, scene.ScrollRange());
        Assert.Contains($"height:calc({2150} * var(--u))", DesignCompiler.Compile(scene));
    }

    [Fact]
    public void A_short_page_is_still_one_screen_long()
    {
        var scene = Minimal();
        scene.Elements.RemoveAll(e => e.Type == "rsvp");
        Assert.Equal(0, scene.ScrollRange());
        Assert.Equal(DesignCanvas.ReferenceViewport, scene.PageHeight());
    }

    [Fact]
    public void A_pinned_element_lengthens_the_page_by_how_long_it_holds()
    {
        var scene = Minimal();
        scene.Elements.Add(new DesignElement
        {
            Id = "held", Type = "shape", X = 0, Y = 600, W = 100, H = 244, Pinned = true, Track = new DesignTrack { Start = 100, End = 2100 },
            Shape = new DesignShape { Kind = "rect", Fill = "#000000" },
        });

        Assert.Equal(2000, scene.ScrollRange());
    }

    [Fact]
    public void Motion_keeps_its_timing_past_the_end_of_the_page()
    {
        var scene = Minimal();
        scene.Elements[0].Track = new DesignTrack { Start = 0, End = 3000 };
        scene.Elements[0].Keyframes = [new() { T = 0, Opacity = 0 }, new() { T = 1, Opacity = 1 }];

        var track = DesignCompiler.TrackOf(scene, scene.Elements[0]);

        Assert.Equal(3000, track.End);
        Assert.Contains("animation-range:0px calc(3000 * var(--u))", DesignCompiler.Compile(scene));
    }

    [Fact]
    public void The_editor_preview_scrolls_one_screen_past_the_end()
    {
        var html = DesignCompiler.Compile(Minimal(), new DesignCompileOptions { EditorPreview = true });
        Assert.Contains($".ib-page{{margin-bottom:calc({DesignCanvas.ReferenceViewport} * var(--u))}}", html);
        Assert.DoesNotContain("margin-bottom", DesignCompiler.Compile(Minimal()));
    }

    [Fact]
    public void A_page_longer_than_the_limit_is_an_error()
    {
        var scene = Minimal();
        scene.Elements[0].Y = DesignCatalog.MaxPageHeight + 10;
        Assert.Contains(DesignValidator.Validate(scene), i => i.Code == "page_too_long");
    }

    [Fact]
    public void A_screens_design_from_the_older_editor_is_converted_without_changing_how_it_plays()
    {
        const string json = """
        {"schema":2,"canvas":{"sections":[{"id":"s1","name":"Screen 1","height":844},{"id":"s2","name":"Screen 2","height":844,"background":"theme:accent"},{"id":"s3","name":"Screen 3","height":844}]},
         "theme":[{"key":"accent","label":"Accent","value":"#b08d57"},{"key":"bg","label":"Background","value":"#ffffff"},{"key":"text","label":"Text","value":"#111111"}],
         "elements":[
           {"id":"late","type":"shape","x":0,"y":1700,"w":100,"h":100,"track":{"start":900,"end":5000},"keyframes":[{"t":0,"opacity":0},{"t":1}],"shape":{"kind":"rect","fill":"#000000"}},
           {"id":"whole","type":"shape","x":0,"y":100,"w":100,"h":100,"keyframes":[{"t":0,"rotate":0},{"t":1,"rotate":90}],"shape":{"kind":"rect","fill":"#000000"}}
         ]}
        """;

        var scene = DesignScene.Parse(json);

        Assert.Equal(DesignScene.CurrentSchema, scene.Schema);
        Assert.Null(scene.Canvas.Sections);
        Assert.DoesNotContain("sections", scene.ToJson());
        // The coloured screen is now a box at the back, where the screen was.
        var ground = scene.Elements[0];
        Assert.Equal(("shape", 844d, 844d, 390d, "theme:accent"), (ground.Type, ground.Y, ground.H, ground.W, ground.Shape!.Fill));
        // Tracks keep the timing the old page gave them.
        Assert.Equal((900d, 1688d), (scene.Elements[1].Track!.Start, scene.Elements[1].Track!.End));
        Assert.Equal((0d, 1688d), (scene.Elements[2].Track!.Start, scene.Elements[2].Track!.End));
        Assert.DoesNotContain(DesignValidator.Validate(scene), i => i.Code is "schema" or "track" or "element_id" or "element_duplicate");
    }

    // ----- Injection: the reason a designed template can skip human review -------------------------

    [Theory]
    [InlineData("red}</style><script>alert(1)</script>")]
    [InlineData("#fff;background:url(https://evil.example/x)")]
    [InlineData("expression(alert(1))")]
    public void Colour_values_that_are_not_colours_never_reach_the_page(string hostile)
    {
        var scene = Minimal();
        scene.Theme.Add(new DesignThemeEntry { Key = "extra", Label = "Extra", Value = hostile });
        scene.Elements[0].Text!.Style.Color = hostile;

        var html = DesignCompiler.Compile(scene);

        Assert.DoesNotContain("alert", html);
        Assert.DoesNotContain("evil.example", html);
        Assert.Contains(DesignValidator.Validate(scene), i => i.Severity == DesignIssue.Error);
    }

    [Fact]
    public void Text_and_labels_are_encoded()
    {
        var scene = Minimal();
        scene.Elements[0].Text!.Runs = [new() { Text = "</p><script>alert(1)</script>" }];
        scene.Roles = ["\"><script>alert(2)</script>"];

        var html = DesignCompiler.Compile(scene);

        Assert.DoesNotContain("<script>alert", html);
    }

    [Fact]
    public void Theme_keys_and_ids_from_the_scene_never_become_selectors()
    {
        var scene = Minimal();
        scene.Theme.Add(new DesignThemeEntry { Key = "x}body{display:none", Label = "x", Value = "#fff" });
        scene.Elements[0].Id = "a}*{display:none}";

        var html = DesignCompiler.Compile(scene);

        Assert.DoesNotContain("display:none", html);
    }

    [Fact]
    public void Image_assets_must_be_strict_base64_rasters()
    {
        Assert.Null(DesignCompiler.ImageDataUri("data:image/svg+xml;base64,PHN2Zz4="));
        Assert.Null(DesignCompiler.ImageDataUri("data:image/png;base64,AAAA\")}body{x:y"));
        Assert.NotNull(DesignCompiler.ImageDataUri("data:image/webp;base64,UklGRg=="));
    }

    [Fact]
    public void The_sanitiser_keeps_drawing_and_drops_everything_that_can_act()
    {
        const string hostile = """
            <svg xmlns="http://www.w3.org/2000/svg" xmlns:xlink="http://www.w3.org/1999/xlink" viewBox="0 0 10 10" onload="alert(1)">
              <script>alert(2)</script>
              <style>*{display:none}</style>
              <foreignObject><div>hi</div></foreignObject>
              <image href="https://evil.example/a.png"/>
              <a href="javascript:alert(3)"><rect width="1" height="1"/></a>
              <use xlink:href="https://evil.example/s.svg#x"/>
              <linearGradient id="g"><stop offset="0" stop-color="gold"/></linearGradient>
              <path d="M0 0L10 10" fill="#AbC" stroke="rgb(255,0,0)" onclick="alert(4)" style="fill:url(https://evil.example);opacity:.5"/>
              <circle r="2" fill="url(#g)"/>
            </svg>
            """;

        var clean = SvgSanitizer.Sanitize(hostile, "p-");

        Assert.DoesNotContain("alert", clean.InnerMarkup);
        Assert.DoesNotContain("evil", clean.InnerMarkup);
        Assert.DoesNotContain("foreignObject", clean.InnerMarkup);
        Assert.DoesNotContain("<a", clean.InnerMarkup);
        Assert.DoesNotContain("<image", clean.InnerMarkup);
        Assert.Contains("id=\"p-g\"", clean.InnerMarkup);
        Assert.Contains("url(#p-g)", clean.InnerMarkup);
        Assert.Equal(["#ffd700", "#aabbcc", "#ff0000"], clean.Colors);
        Assert.Contains("fill:var(--c1, #aabbcc)", clean.InnerMarkup);
        Assert.Equal("0 0 10 10", clean.ViewBox);
    }

    [Fact]
    public void Sanitising_twice_is_stable()
    {
        var once = SvgSanitizer.Sanitize("""<svg xmlns="http://www.w3.org/2000/svg" width="20" height="10"><rect width="5" height="5" fill="#123456"/><circle r="1" fill="#654321"/></svg>""");
        var twice = SvgSanitizer.Sanitize(once.Document);

        Assert.Equal(once.InnerMarkup, twice.InnerMarkup);
        Assert.Equal(once.Colors, twice.Colors);
    }

    [Fact]
    public void Doctypes_are_refused()
    {
        const string xxe = """<?xml version="1.0"?><!DOCTYPE svg [<!ENTITY x SYSTEM "file:///etc/passwd">]><svg xmlns="http://www.w3.org/2000/svg">&x;</svg>""";
        Assert.Throws<SvgRejectedException>(() => SvgSanitizer.Sanitize(xxe));
    }

    [Fact]
    public void Check_requires_an_rsvp_button()
    {
        var scene = Minimal();
        scene.Elements.RemoveAll(e => e.Type == "rsvp");
        Assert.Contains(DesignValidator.Validate(scene), i => i.Code == "rsvp_required");
    }

    [Fact]
    public void Check_rejects_a_dropdown_with_no_options()
    {
        var scene = Minimal();
        scene.Fields.Add(new DesignCustomField { Path = "event.meal", Label = "Meal", Type = "select" });
        Assert.Contains(DesignValidator.Validate(scene), i => i.Code == "select_options");
    }

    [Fact]
    public void Check_flags_hardcoded_colours_as_warnings_only()
    {
        var scene = Minimal();
        scene.Elements[0].Text!.Style.Color = "#ff0000";
        var issue = Assert.Single(DesignValidator.Validate(scene), i => i.Code == "hardcoded_color");
        Assert.Equal(DesignIssue.Warning, issue.Severity);
    }

    [Fact]
    public void Editor_preview_adds_the_bridge_and_a_published_compile_never_does()
    {
        var scene = Minimal();
        Assert.Contains("ib:scrolled", DesignCompiler.Compile(scene, new DesignCompileOptions { EditorPreview = true }));
        Assert.DoesNotContain("ib:scrolled", DesignCompiler.Compile(scene));
    }

    private static DesignScene Minimal()
    {
        var scene = DesignStarters.Create("blank")!;
        scene.Elements = scene.Elements.Where(e => e.Type is "rsvp").ToList();
        scene.Elements.Insert(0, new DesignElement
        {
            Id = "t", Type = "text", X = 10, Y = 20, W = 200, H = 40,
            Text = new DesignText { Runs = [new() { Text = "Hello" }], Style = new DesignTypography { Color = "theme:text" } },
        });
        return scene;
    }
}
