using InvitesBlog.TemplateCompiler.Design;
using InvitesBlog.TemplateCompiler.Design.Art;
using Xunit;

namespace InvitesBlog.Tests;

public class SvgArtConverterTests
{
    private const string Ns = "xmlns=\"http://www.w3.org/2000/svg\"";

    private static void AllSanitize(ArtPlan plan)
    {
        foreach (var layer in plan.Layers)
        {
            var clean = SvgSanitizer.Sanitize(layer.Svg);
            Assert.DoesNotContain("animate", clean.InnerMarkup);
            Assert.True(layer.Frames.Count <= DesignCatalog.MaxKeyframes, $"{layer.Frames.Count} keyframes");
        }
    }

    [Fact]
    public void A_still_svg_is_one_still_layer_with_its_class_colours_kept()
    {
        var plan = SvgArtConverter.Convert($"""
            <svg {Ns} viewBox="0 0 100 50"><style>.a {"{"} fill: #ff0000 {"}"}</style>
              <rect class="a" x="0" y="0" width="100" height="50"/></svg>
            """);
        Assert.False(plan.Animated);
        var layer = Assert.Single(plan.Layers);
        Assert.Empty(layer.Frames);
        Assert.Equal(100, plan.Width);
        Assert.Equal(50, plan.Height);
        var clean = SvgSanitizer.Sanitize(layer.Svg);
        Assert.Contains("#ff0000", clean.Colors);
    }

    [Fact]
    public void A_smil_spin_becomes_rotation_keyframes_about_the_art_centre()
    {
        var plan = SvgArtConverter.Convert($"""
            <svg {Ns} viewBox="0 0 100 100">
              <rect width="100" height="100" fill="#eeeeee"/>
              <g id="wheel"><rect x="40" y="10" width="20" height="20" fill="#000000"/>
                <animateTransform attributeName="transform" type="rotate" from="0 50 50" to="360 50 50" dur="1s" repeatCount="indefinite"/></g>
            </svg>
            """);
        Assert.True(plan.Animated);
        Assert.Equal(3, plan.Loops);
        Assert.Equal(2, plan.Layers.Count);
        Assert.Empty(plan.Layers[0].Frames);
        var spin = plan.Layers[1];
        Assert.Equal("wheel", spin.Name);
        Assert.Equal(0, spin.Frames[0].Rotate, 1);
        Assert.Equal(1080, spin.Frames[^1].Rotate, 0);
        // Rotating about the art's own centre: nothing drifts.
        Assert.All(spin.Frames, f => { Assert.Equal(0, f.Dx, 2); Assert.Equal(0, f.Dy, 2); Assert.Equal(1, f.Scale, 3); });
        Assert.True(spin.Frames.Count <= 4, "a steady spin needs only its ends");
        AllSanitize(plan);
    }

    [Fact]
    public void Rotation_about_another_point_comes_out_as_rotation_plus_the_centre_travelling()
    {
        var plan = SvgArtConverter.Convert($"""
            <svg {Ns} viewBox="0 0 100 100">
              <circle cx="10" cy="10" r="5"><animateTransform attributeName="transform" type="rotate" from="0 0 0" to="180 0 0" dur="2s" fill="freeze"/></circle>
            </svg>
            """);
        var layer = Assert.Single(plan.Layers);
        var end = layer.Frames[^1];
        Assert.Equal(180, end.Rotate, 1);
        // The art centre (50,50) turned 180° about (0,0) lands at (-50,-50).
        Assert.Equal(-100, end.Dx, 1);
        Assert.Equal(-100, end.Dy, 1);
        Assert.Equal(2, plan.Seconds, 3);
    }

    [Fact]
    public void A_css_keyframe_spin_with_a_centred_fill_box_origin_turns_about_the_shape()
    {
        var plan = SvgArtConverter.Convert($$"""
            <svg {{Ns}} viewBox="0 0 200 100">
              <style>
                @keyframes spin { from { transform: rotate(0deg) } to { transform: rotate(1turn) } }
                .gear { animation: spin 3s linear infinite; transform-origin: center; transform-box: fill-box; }
              </style>
              <rect class="gear" x="0" y="0" width="100" height="100"/>
            </svg>
            """);
        var layer = Assert.Single(plan.Layers);
        Assert.Equal(1, plan.Loops);
        Assert.Equal(360, layer.Frames[^1].Rotate, 0);
        // Halfway, turned 180° about (50,50): the art's centre (100,50) swings to (0,50).
        var mid = layer.Frames.MinBy(f => Math.Abs(f.T - 0.5))!;
        Assert.Equal(0.5, mid.T, 2);
        Assert.Equal(-100, mid.Dx, 0);
        AllSanitize(plan);
    }

    [Fact]
    public void Motion_along_a_path_follows_it()
    {
        var plan = SvgArtConverter.Convert($"""
            <svg {Ns} viewBox="0 0 100 100">
              <circle cx="0" cy="0" r="4"><animateMotion path="M10,10 L90,10 L90,90" dur="2s" fill="freeze"/></circle>
            </svg>
            """);
        var frames = Assert.Single(plan.Layers).Frames;
        Assert.Equal(10, frames[0].Dx, 1);
        Assert.Equal(10, frames[0].Dy, 1);
        Assert.Equal(90, frames[^1].Dx, 1);
        Assert.Equal(90, frames[^1].Dy, 1);
        // The corner is a keyframe: (90,10) halfway along.
        Assert.Contains(frames, f => Math.Abs(f.Dx - 90) < 1 && Math.Abs(f.Dy - 10) < 1 && Math.Abs(f.T - 0.5) < 0.02);
    }

    [Fact]
    public void A_blink_is_opacity_and_the_static_hidden_state_is_lifted_from_the_layer()
    {
        var plan = SvgArtConverter.Convert($"""
            <svg {Ns} viewBox="0 0 10 10">
              <circle cx="5" cy="5" r="5" opacity="0"><animate attributeName="opacity" values="0;1;0" dur="1s" repeatCount="indefinite"/></circle>
            </svg>
            """);
        var layer = Assert.Single(plan.Layers);
        Assert.DoesNotContain("opacity", layer.Svg);
        Assert.Equal(0, layer.Frames[0].Opacity, 2);
        Assert.Contains(layer.Frames, f => f.Opacity > 0.95);
    }

    [Fact]
    public void A_colour_change_becomes_a_flipbook()
    {
        var plan = SvgArtConverter.Convert($"""
            <svg {Ns} viewBox="0 0 10 10">
              <rect width="10" height="10" fill="#ff0000"><animate attributeName="fill" from="#ff0000" to="#0000ff" dur="2s" fill="freeze"/></rect>
            </svg>
            """, maxFlipFrames: 4);
        Assert.Equal(4, plan.Layers.Count);
        Assert.Contains("#ff0000", plan.Layers[0].Svg);
        Assert.Contains("#bf0040", plan.Layers[1].Svg);
        // One still showing at a time: the first at the start, the last at the end.
        Assert.Equal(1, plan.Layers[0].Frames[0].Opacity);
        Assert.Equal(0, plan.Layers[1].Frames[0].Opacity);
        Assert.Equal(1, plan.Layers[^1].Frames[^1].Opacity);
        Assert.Equal(0, plan.Layers[0].Frames[^1].Opacity);
        AllSanitize(plan);
    }

    [Fact]
    public void Paint_order_survives_a_moving_part_in_the_middle()
    {
        var plan = SvgArtConverter.Convert($"""
            <svg {Ns} viewBox="0 0 10 10">
              <rect width="10" height="10" fill="#111111"/>
              <circle cx="5" cy="5" r="2" fill="#222222"><animate attributeName="cx" from="2" to="8" dur="1s" fill="freeze"/></circle>
              <rect width="3" height="3" fill="#333333"/>
            </svg>
            """);
        Assert.Equal(3, plan.Layers.Count);
        Assert.Contains("#111111", plan.Layers[0].Svg);
        Assert.Contains("#222222", plan.Layers[1].Svg);
        Assert.Contains("#333333", plan.Layers[2].Svg);
        Assert.Equal(-3, plan.Layers[1].Frames[0].Dx, 2);
        Assert.Equal(3, plan.Layers[1].Frames[^1].Dx, 2);
    }

    [Fact]
    public void An_animation_in_a_reused_symbol_turns_the_whole_picture_into_a_flipbook()
    {
        var plan = SvgArtConverter.Convert($"""
            <svg {Ns} viewBox="0 0 10 10">
              <defs><circle id="dot" cx="5" cy="5" r="2"><animate attributeName="r" from="1" to="4" dur="1s" repeatCount="indefinite"/></circle></defs>
              <use href="#dot"/>
            </svg>
            """, maxFlipFrames: 3);
        Assert.Equal(3, plan.Layers.Count);
        Assert.All(plan.Layers, l => Assert.Contains("<use", l.Svg));
        Assert.Contains("r=\"1\"", plan.Layers[0].Svg);
        AllSanitize(plan);
    }

    [Fact]
    public void Event_triggered_animations_never_start_so_the_art_is_still()
    {
        var plan = SvgArtConverter.Convert($"""
            <svg {Ns} viewBox="0 0 10 10"><rect width="10" height="10"><animate attributeName="x" to="5" begin="click" dur="1s"/></rect></svg>
            """);
        Assert.False(plan.Animated);
    }

    [Fact]
    public void Digits_nobody_can_see_are_dropped()
    {
        var plan = SvgArtConverter.Convert($"""
            <svg {Ns} viewBox="0 0 500 500"><path d="M1.23456,2.34567L100.98765.5"/></svg>
            """);
        // Relative, one decimal on a 500-unit drawing, separators only where a number needs one; after a moveto, pairs are linetos.
        Assert.Equal("m1.2 2.3 99.8-1.8", System.Text.RegularExpressions.Regex.Match(plan.Layers[0].Svg, "d=\"([^\"]*)\"").Groups[1].Value);
    }

    [Fact]
    public void An_inkscape_doctype_and_illustrator_namespace_entities_are_read()
    {
        var inkscape = SvgArtConverter.Convert("""
            <?xml version="1.0"?>
            <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd">
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 10 10"><rect width="10" height="10" fill="#123456"/></svg>
            """);
        Assert.Contains("#123456", inkscape.Layers[0].Svg);

        var illustrator = SvgArtConverter.Convert("""
            <!DOCTYPE svg PUBLIC "-//W3C//DTD SVG 1.1//EN" "http://www.w3.org/Graphics/SVG/1.1/DTD/svg11.dtd" [
              <!ENTITY ns_svg "http://www.w3.org/2000/svg">
              <!ENTITY ns_xlink "http://www.w3.org/1999/xlink">
            ]>
            <svg xmlns="&ns_svg;" xmlns:xlink="&ns_xlink;" viewBox="0 0 10 10"><rect width="10" height="10" fill="#654321"/></svg>
            """);
        Assert.Contains("#654321", SvgSanitizer.Sanitize(illustrator.Layers[0].Svg).Colors);
    }

    [Fact]
    public void Entities_that_could_nest_or_reach_outside_are_still_refused()
    {
        Assert.Throws<ArtRejectedException>(() => SvgArtConverter.Convert(
            "<!DOCTYPE svg [<!ENTITY a \"x\"><!ENTITY b \"&a;&a;&a;\">]><svg xmlns=\"http://www.w3.org/2000/svg\">&b;</svg>"));
        Assert.Throws<ArtRejectedException>(() => SvgArtConverter.Convert(
            "<!DOCTYPE svg [<!ENTITY % p SYSTEM \"http://evil/x\"> %p;]><svg xmlns=\"http://www.w3.org/2000/svg\"/>"));
    }

    [Theory]
    [InlineData("M10.123456,20.654321 L30.5,40.25 H50 V60 C70,80 90,100 110,120 S150,160 170,180 Q190,200 210,220 T250,260 A30,40 15 1,0 300,310 Z m5,5 l10,0 l0,10 z", 2)]
    [InlineData("m0,0 1,1 2,2 .5.5-3-3", 1)]
    [InlineData("M0 0h100v100h-100zM20 20l60 0 0 60-60 0z", 0)]
    public void Compacted_path_data_draws_the_same_shape(string d, int decimals)
    {
        var compact = PathData.Compact(d, decimals);
        Assert.True(compact.Length <= d.Length, compact);
        var a = PathGeometry.Parse(d).Polylines.SelectMany(l => l).ToList();
        var b = PathGeometry.Parse(compact).Polylines.SelectMany(l => l).ToList();
        Assert.Equal(a.Count, b.Count);
        var tolerance = Math.Pow(10, -decimals) * 1.01;
        for (var i = 0; i < a.Count; i++)
        {
            Assert.True(Math.Abs(a[i].X - b[i].X) <= tolerance * 2 && Math.Abs(a[i].Y - b[i].Y) <= tolerance * 2,
                $"point {i}: {a[i]} vs {b[i]} in {compact}");
        }
    }

    [Fact]
    public void Rounding_does_not_drift_along_a_long_path()
    {
        var d = "M0,0" + string.Concat(Enumerable.Range(0, 2000).Select(_ => "l0.3333,0.3333"));
        var end = PathGeometry.Parse(PathData.Compact(d, 1)).Polylines[0][^1];
        Assert.Equal(666.6, end.X, 0);
        Assert.Equal(666.6, end.Y, 0);
    }

    [Fact]
    public void A_heavy_svg_is_cut_into_layers_under_the_limit_in_paint_order()
    {
        var paths = string.Concat(Enumerable.Range(0, 3000).Select(i =>
            $"<path fill=\"#{i % 256:x2}0000\" d=\"M{i * 0.123456:0.######},{i * 0.654321:0.######} L{i + 10.123456},{i + 20.654321} L{i + 3.3},{i + 7.7} Z\"/>"));
        var markup = $"<svg {Ns} viewBox=\"0 0 4000 4000\"><g id=\"layer1\" inkscape=\"x\"><g>{paths}</g></g></svg>";
        var plan = SvgArtConverter.Convert(markup, maxLayerBytes: 40 * 1024);
        Assert.True(plan.Layers.Count > 1, $"{plan.Layers.Count} layers");
        Assert.All(plan.Layers, l => Assert.True(System.Text.Encoding.UTF8.GetByteCount(l.Svg) <= 40 * 1024 || l.Svg.Split("<path").Length == 2));
        // Every path is in exactly one layer, and the order is kept.
        var fills = plan.Layers.SelectMany(l => System.Text.RegularExpressions.Regex.Matches(l.Svg, "fill=\"#([0-9a-f]{2})0000\"").Select(m => m.Groups[1].Value)).ToList();
        Assert.Equal(Enumerable.Range(0, 3000).Select(i => (i % 256).ToString("x2")), fills);
        // Nothing left of the unused id or the empty nesting.
        Assert.DoesNotContain("layer1", plan.Layers[0].Svg);
        Assert.DoesNotContain("<g>", plan.Layers[0].Svg);
        Assert.All(plan.Layers, l => SvgSanitizer.Sanitize(l.Svg));
    }

    [Fact]
    public void Hidden_parts_are_dropped_unless_something_uses_them()
    {
        var plan = SvgArtConverter.Convert($"""
            <svg {Ns} viewBox="0 0 10 10">
              <g style="display:none"><rect width="1" height="1" fill="#111111"/></g>
              <rect id="tpl" width="2" height="2" fill="#222222" visibility="hidden"/>
              <use href="#tpl"/>
              <circle r="3" fill="#333333"/>
            </svg>
            """);
        var svg = string.Concat(plan.Layers.Select(l => l.Svg));
        Assert.DoesNotContain("#111111", svg);
        Assert.Contains("#222222", svg);
        Assert.Contains("#333333", svg);
    }

    [Fact]
    public void Doctypes_are_refused()
    {
        Assert.Throws<ArtRejectedException>(() => SvgArtConverter.Convert(
            "<!DOCTYPE svg [<!ENTITY x SYSTEM \"file:///etc/passwd\">]><svg xmlns=\"http://www.w3.org/2000/svg\">&x;</svg>"));
    }
}
