using System.Text;
using System.Text.RegularExpressions;
using InvitesBlog.TemplateCompiler.Design;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Making things without importing art: ready-made shapes, gradients, outlines, curved text, pictures
/// in shapes and in letters, shadows, an element's own light, and photo adjustments.
/// </summary>
public class DesignCreateToolsTests
{
    private static DesignScene Scene(params DesignElement[] elements)
    {
        var scene = DesignStarters.Create("blank")!;
        scene.Stage = false;
        scene.Elements.Clear();
        scene.Elements.AddRange(elements);
        scene.Assets["pic"] = new DesignAsset { Kind = "image", Data = "data:image/png;base64,iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==" };
        return scene;
    }

    private static DesignElement Shape(string kind, double w = 100, double h = 50) => new()
    {
        Id = kind, Type = "shape", X = 0, Y = 0, W = w, H = h, Shape = new DesignShape { Kind = kind, Fill = "#336699" },
    };

    private static DesignGradient TwoColour(string? type = null) => new()
    {
        Type = type, Stops = [new() { Color = "theme:accent" }, new() { Color = "#FF0000" }],
    };

    private static string Filter(string html, int index) => Regex.Match(html, $"<filter id=\"f{index}\"[^>]*>.*?</filter>").Value;

    [Fact]
    public void Every_ready_made_shape_is_a_path_in_its_box()
    {
        foreach (var kind in DesignShapes.ExtraKinds)
        {
            var html = DesignCompiler.Compile(Scene(Shape(kind)));
            var d = Regex.Match(html, "<path d=\"([^\"]+)\" fill-rule=\"evenodd\" style=\"fill:#336699\"/>");
            Assert.True(d.Success, kind);
            // Only numbers and path commands: nothing from the scene reaches the outline.
            Assert.Matches("^[MLHVCQTAZ0-9 .-]+$", d.Groups[1].Value);
            Assert.DoesNotContain(DesignValidator.Validate(Scene(Shape(kind))), i => i.Code == "shape_kind");
        }
    }

    [Fact]
    public void A_star_has_twice_its_points_and_its_count_is_held_in_range()
    {
        Assert.Equal(10, Regex.Matches(DesignShapes.D("star", 100, 100, 5, null, 0)!, "[ML]").Count);
        Assert.Equal(48, Regex.Matches(DesignShapes.D("star", 100, 100, 99, null, 0)!, "[ML]").Count);
        Assert.Equal("M0 50A50 50 0 1 1 100 50A50 50 0 1 1 0 50Z", DesignShapes.D("ellipse", 100, 100, 0, null, 0));
    }

    [Fact]
    public void Unknown_shapes_and_outline_styles_fail_validation()
    {
        var bad = Shape("blob");
        bad.Shape!.Dash = "wiggly";
        var issues = DesignValidator.Validate(Scene(bad));
        Assert.Equal(2, issues.Count(i => i.Code == "shape_kind" && i.Severity == "error"));
    }

    [Theory]
    [InlineData("dashed", "stroke-dasharray:6 4")]
    [InlineData("dotted", "stroke-dasharray:0 4;stroke-linecap:round")]
    public void Outlines_can_be_dashed_or_dotted(string dash, string css)
    {
        var el = Shape("rect");
        el.Shape!.Stroke = "#000";
        el.Shape.StrokeWidth = 2;
        el.Shape.Dash = dash;
        Assert.Contains(css, DesignCompiler.Compile(Scene(el)));
    }

    [Fact]
    public void A_shape_gradient_is_an_svg_gradient_with_theme_colours_kept_as_variables()
    {
        var el = Shape("heart");
        el.Gradient = TwoColour();
        var html = DesignCompiler.Compile(Scene(el));
        Assert.Contains("<defs><linearGradient id=\"g0\" gradientUnits=\"userSpaceOnUse\" x1=\"50\" y1=\"0\" x2=\"50\" y2=\"50\"><stop offset=\"0\" style=\"stop-color:var(--ib-accent)\"/><stop offset=\"1\" style=\"stop-color:#ff0000\"/></linearGradient></defs>", html);
        Assert.Contains("style=\"fill:url(#g0)\"", html);
    }

    [Fact]
    public void A_gradient_needs_two_colours_that_resolve()
    {
        var el = Shape("rect");
        el.Gradient = new DesignGradient { Stops = [new() { Color = "#000" }, new() { Color = "url(x)" }] };
        var scene = Scene(el);
        Assert.DoesNotContain("Gradient", DesignCompiler.Compile(scene));
        Assert.Contains(DesignValidator.Validate(scene), i => i.Code == "color");
    }

    [Fact]
    public void Text_buttons_pictures_and_illustrations_take_the_gradient_as_css()
    {
        var text = new DesignElement { Id = "t", Type = "text", W = 200, H = 40, Gradient = TwoColour("radial"), Text = new DesignText { Runs = [new() { Text = "Hi" }] } };
        var html = DesignCompiler.Compile(Scene(text));
        Assert.Contains("background-image:radial-gradient(ellipse farthest-corner,var(--ib-accent) 0%,#ff0000 100%);-webkit-background-clip:text;background-clip:text;-webkit-text-fill-color:transparent", html);

        var picture = new DesignElement { Id = "p", Type = "image", W = 100, H = 100, Gradient = TwoColour(), Image = new DesignImage { Asset = "pic" } };
        var pageWithPicture = DesignCompiler.Compile(Scene(picture));
        // The picture becomes the gradient's silhouette, through its own pixels.
        Assert.Contains("background-image:linear-gradient(180deg,var(--ib-accent) 0%,#ff0000 100%);background-size:100% 100%;-webkit-mask:var(--im) center/cover no-repeat", pageWithPicture);
        Assert.Contains("--im:url(", pageWithPicture);
    }

    [Fact]
    public void A_picture_inside_letters_clips_the_picture_to_the_text()
    {
        var text = new DesignElement { Id = "t", Type = "text", W = 200, H = 40, Text = new DesignText { Image = "pic", Runs = [new() { Text = "Hi" }] } };
        var html = DesignCompiler.Compile(Scene(text));
        Assert.Contains("<p class=\"t i0\">", html);
        Assert.Contains("background-size:cover;background-position:center;-webkit-background-clip:text", html);
        text.Text.Image = "nothing";
        Assert.Contains(DesignValidator.Validate(Scene(text)), i => i.Code == "asset_missing");
    }

    [Fact]
    public void Curved_text_runs_along_an_arc_and_keeps_its_fields()
    {
        var text = new DesignElement
        {
            Id = "t", Type = "text", W = 300, H = 80,
            Text = new DesignText { Curve = 50, Runs = [new() { Text = "Dear " }, new() { Var = "guest.name", Bold = true }], Style = new DesignTypography { Size = 20, Stroke = "#fff", StrokeWidth = 1 } },
        };
        var html = DesignCompiler.Compile(Scene(text));
        Assert.Matches("<defs><path id=\"c0\" d=\"M[0-9. -]+A[0-9. ]+ 0 0 1 [0-9. -]+\"/></defs><text class=\"t\"><textPath href=\"#c0\" startOffset=\"50%\" text-anchor=\"middle\">Dear <tspan style=\"font-weight:700\"><tspan data-var=\"guest.name\"", html);
        Assert.Contains("font-size:20px;", html);
        Assert.Contains("stroke:#fff;stroke-width:2;paint-order:stroke fill", html);
        // Straight text stays a paragraph, and a curve stops it being split into pieces.
        text.Text.Curve = 0;
        Assert.Contains("<p class=\"t\">", DesignCompiler.Compile(Scene(text)));
    }

    [Fact]
    public void A_curve_past_halfway_takes_the_long_way_round()
    {
        var text = new DesignElement { Id = "t", Type = "text", W = 300, H = 80, Text = new DesignText { Curve = -80, Runs = [new() { Text = "x" }] } };
        Assert.Matches(" 0 1 0 [0-9.]+ [0-9.-]+\"/>", DesignCompiler.Compile(Scene(text)));
    }

    [Fact]
    public void Text_outlines_are_a_text_stroke_behind_the_letters()
    {
        var text = new DesignElement { Id = "t", Type = "text", W = 200, H = 40, Text = new DesignText { Runs = [new() { Text = "Hi" }], Style = new DesignTypography { Stroke = "theme:accent", StrokeWidth = 1.5 } } };
        Assert.Contains("-webkit-text-stroke:calc(3 * var(--u)) var(--ib-accent);paint-order:stroke fill;", DesignCompiler.Compile(Scene(text)));
    }

    [Fact]
    public void Pictures_photos_illustrations_and_groups_can_be_clipped_to_a_shape()
    {
        var picture = new DesignElement { Id = "p", Type = "image", W = 100, H = 80, Mask = new DesignMask { Kind = "arch" }, Image = new DesignImage { Asset = "pic" } };
        var html = DesignCompiler.Compile(Scene(picture));
        Assert.Contains("<div class=\"a\"><div class=\"m m0\"><div class=\"i0\"", html);
        var data = Regex.Match(html, @"\.m0\{position:relative;width:100%;height:100%;-webkit-mask:url\(""data:image/svg\+xml;base64,([^""]+)""\) 0 0/100% 100% no-repeat;mask:").Groups[1].Value;
        Assert.Equal("<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 100 80\" preserveAspectRatio=\"none\"><path d=\"M0 80V50A50 50 0 0 1 100 50V80Z\" fill-rule=\"evenodd\"/></svg>",
            Encoding.UTF8.GetString(Convert.FromBase64String(data)));

        var shape = Shape("rect");
        shape.Mask = new DesignMask { Kind = "heart" };
        Assert.DoesNotContain("class=\"m m0\"", DesignCompiler.Compile(Scene(shape)));
        Assert.Contains(DesignValidator.Validate(Scene(shape)), i => i.Code == "mask" && i.Severity == "warning");
    }

    [Fact]
    public void A_drop_shadow_goes_under_everything_else()
    {
        var el = Shape("star");
        el.Shadow = new DesignShadow { X = 3, Y = 5, Blur = 6, Color = "theme:text" };
        el.OuterGlow = new DesignGlow();
        var f = Filter(DesignCompiler.Compile(Scene(el)), 0);
        Assert.Contains("<feGaussianBlur in=\"SourceAlpha\" stdDeviation=\"3\"/><feOffset dx=\"3\" dy=\"5\" result=\"sa\"/><feFlood style=\"flood-color:var(--ib-text)\" flood-opacity=\"0.35\"/>", f);
        Assert.EndsWith("<feMerge><feMergeNode in=\"sh\"/><feMergeNode in=\"og\"/><feMergeNode in=\"SourceGraphic\"/></feMerge></filter>", f);
    }

    [Fact]
    public void An_elements_light_turns_and_colours_its_bevel_and_embossed_pattern()
    {
        var el = Shape("rect");
        el.Bevel = new DesignBevel();
        el.Pattern = new DesignPattern { Kind = "dots", Mode = "emboss" };
        el.Light = new DesignLight { Angle = 45, Color = "theme:accent" };
        var f = Filter(DesignCompiler.Compile(Scene(el)), 0);
        Assert.Equal(2, Regex.Matches(f, "azimuth=\"315\"").Count);
        Assert.Equal(2, Regex.Matches(f, "<feFlood style=\"flood-color:var\\(--ib-accent\\)\"/><feComposite in=\"lw\" operator=\"arithmetic\" k1=\"1\" k2=\"0\" k3=\"-0.5\" k4=\"0.5\"").Count);

        // The default light (top left, white) changes nothing.
        var plain = Shape("rect");
        plain.Bevel = new DesignBevel();
        var lit = Shape("rect");
        lit.Bevel = new DesignBevel();
        lit.Light = new DesignLight { Angle = 135, Color = "#FFFFFF" };
        Assert.Equal(DesignCompiler.Compile(Scene(plain)), DesignCompiler.Compile(Scene(lit)));
    }

    [Fact]
    public void Photo_adjustments_are_css_filters_and_duotone_and_grain_are_in_the_element_filter()
    {
        var picture = new DesignElement
        {
            Id = "p", Type = "image", W = 100, H = 80,
            Image = new DesignImage { Asset = "pic", Adjust = new DesignAdjust { Brightness = 1.2, Saturation = 0, Sepia = 0.4, Grain = 0.3, Duotone = new DesignDuotone { Dark = "#000080", Light = "theme:accent" } } },
        };
        var html = DesignCompiler.Compile(Scene(picture));
        Assert.Contains(";filter:brightness(1.2) saturate(0) sepia(0.4)}", html);
        var f = Filter(html, 0);
        Assert.StartsWith("<filter id=\"f0\" x=\"-10%\" y=\"-25%\" width=\"120%\" height=\"150%\" color-interpolation-filters=\"sRGB\"><feColorMatrix in=\"SourceGraphic\" type=\"luminanceToAlpha\" result=\"dl\"/><feFlood style=\"flood-color:#000080\"/>", f);
        Assert.Contains("<feBlend in2=\"dt\" mode=\"soft-light\"/>", f);
        var slot = new DesignElement { Id = "s", Type = "slot", W = 100, H = 80, Slot = new DesignSlot { Adjust = new DesignAdjust { Contrast = 1.5 } } };
        Assert.Contains("object-fit:cover;filter:contrast(1.5)}", DesignCompiler.Compile(Scene(slot)));
    }

    [Fact]
    public void Settings_that_are_not_numbers_fail_validation()
    {
        var el = Shape("star");
        el.Shape!.Amount = double.NaN;
        el.Shadow = new DesignShadow { Blur = double.PositiveInfinity };
        el.Light = new DesignLight { Angle = double.NaN };
        el.Gradient = new DesignGradient { Type = "conic", Angle = double.NaN, Stops = [new() { Color = "#000" }] };
        var issues = DesignValidator.Validate(Scene(el)).Select(i => i.Message).ToList();
        Assert.Contains(issues, m => m.Contains("shape setting"));
        Assert.Contains(issues, m => m.Contains("shadow setting"));
        Assert.Contains(issues, m => m.Contains("light angle"));
        Assert.Contains(issues, m => m.Contains("gradient type"));
        Assert.Contains(issues, m => m.Contains("2 to 6 colours"));
        Assert.Contains(issues, m => m.Contains("gradient angle"));
    }
}
