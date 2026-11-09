using System.Text;
using System.Text.RegularExpressions;
using InvitesBlog.TemplateCompiler.Design;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// A shape filled with particles (dust, stars, glitter…): the canvas the page's particle script draws on,
/// clipped to the shape and under its outline, the script only on pages that need it, and Check's rules.
/// </summary>
public class DesignParticlesTests
{
    private static DesignScene Scene(params DesignElement[] elements)
    {
        var scene = DesignStarters.Create("blank")!;
        scene.Stage = false;
        scene.Elements.Clear();
        scene.Elements.AddRange(elements);
        return scene;
    }

    private static DesignElement Shape(string kind, DesignParticles? particles, string? stroke = null, double radius = 0) => new()
    {
        Id = "p", Type = "shape", X = 0, Y = 0, W = 200, H = 100,
        Shape = new DesignShape { Kind = kind, Fill = "theme:accent", Stroke = stroke, StrokeWidth = stroke is null ? 0 : 2, Radius = radius, Particles = particles },
    };

    private static string Canvas(string html) => Regex.Match(html, "<canvas class=\"pt\"[^>]*></canvas>").Value;

    [Fact]
    public void Particles_take_the_place_of_the_fill_and_sit_under_the_outline()
    {
        var html = DesignCompiler.Compile(Scene(Shape("star", new DesignParticles { Kind = "glitter", Amount = 0.85, Size = 0.2, Speed = 0.15, Pointer = "avoid" }, stroke: "#222222")));
        var canvas = Canvas(html);
        Assert.Contains("data-pt=\"glitter 0.85 0.2 0.15 avoid\"", canvas);
        // The shape's fill colour (a theme colour, so it follows the theme) is the particles'.
        Assert.Contains("color:var(--ib-accent)", canvas);
        // Clipped to the star, in single quotes inside the double-quoted style attribute.
        Assert.Matches("-webkit-mask:url\\('data:image/svg\\+xml;base64,[A-Za-z0-9+/=]+'\\) 0 0/100% 100% no-repeat", canvas);
        Assert.DoesNotContain("url(\"", canvas);
        // The fill is gone; the outline is drawn after (over) the canvas.
        Assert.Contains("style=\"fill:transparent;stroke:#222222", html);
        Assert.True(html.IndexOf("<canvas class=\"pt\"", StringComparison.Ordinal) < html.IndexOf("<svg class=\"s\"", StringComparison.Ordinal));
        Assert.Contains("canvas.pt", html); // the particle script
    }

    [Fact]
    public void Unset_settings_are_the_middle_and_out_of_range_ones_are_held_to_0_to_1()
    {
        var plain = Canvas(DesignCompiler.Compile(Scene(Shape("ellipse", new DesignParticles { Kind = "dust" }))));
        Assert.Contains("data-pt=\"dust 0.5 0.5 0.5 none\"", plain);
        var wild = Canvas(DesignCompiler.Compile(Scene(Shape("ellipse", new DesignParticles { Kind = "snow", Amount = 7, Size = -2, Pointer = "wobble" }))));
        Assert.Contains("data-pt=\"snow 1 0 0.5 none\"", wild);
    }

    [Fact]
    public void Its_own_colour_wins_over_the_fill()
    {
        var canvas = Canvas(DesignCompiler.Compile(Scene(Shape("rect", new DesignParticles { Kind = "stars", Color = "#ffd27a" }))));
        Assert.Contains("color:#ffd27a", canvas);
    }

    [Fact]
    public void A_plain_box_needs_no_clip_and_a_rounded_one_does()
    {
        Assert.DoesNotContain("mask", Canvas(DesignCompiler.Compile(Scene(Shape("rect", new DesignParticles { Kind = "dust" })))));
        Assert.Contains("mask:url(", Canvas(DesignCompiler.Compile(Scene(Shape("rect", new DesignParticles { Kind = "dust" }, radius: 20)))));
    }

    [Fact]
    public void Pages_without_particles_carry_no_particle_script_and_lines_never_get_particles()
    {
        var plain = DesignCompiler.Compile(Scene(Shape("star", null)));
        Assert.DoesNotContain("canvas.pt", plain);
        Assert.DoesNotContain("<canvas", plain);

        var line = DesignCompiler.Compile(Scene(Shape("line", new DesignParticles { Kind = "dust" }, stroke: "#000000")));
        Assert.DoesNotContain("<canvas", line);
        Assert.DoesNotContain("canvas.pt", line);
        Assert.Contains(DesignValidator.Validate(Scene(Shape("line", new DesignParticles { Kind = "dust" }, stroke: "#000000"))), i => i.Code == "particles" && i.Severity == "warning");

        var unknown = DesignCompiler.Compile(Scene(Shape("star", new DesignParticles { Kind = "lasers" })));
        Assert.DoesNotContain("<canvas", unknown);
    }

    [Fact]
    public void Check_refuses_unknown_kinds_pointer_effects_and_settings_outside_0_to_1()
    {
        Assert.Contains(DesignValidator.Validate(Scene(Shape("star", new DesignParticles { Kind = "lasers" }))), i => i.Code == "particles" && i.Severity == "error");
        Assert.Contains(DesignValidator.Validate(Scene(Shape("star", new DesignParticles { Kind = "dust", Pointer = "wobble" }))), i => i.Code == "particles");
        Assert.Contains(DesignValidator.Validate(Scene(Shape("star", new DesignParticles { Kind = "dust", Speed = 1.5 }))), i => i.Code == "particles");
        Assert.DoesNotContain(DesignValidator.Validate(Scene(Shape("star", new DesignParticles { Kind = "fireflies", Amount = 1, Size = 0, Speed = 0.3, Pointer = "swirl" }))), i => i.Code == "particles");
    }

    [Fact]
    public void Every_kind_compiles_and_passes_check()
    {
        foreach (var kind in DesignParticleKinds.All)
        {
            var scene = Scene(Shape("heart", new DesignParticles { Kind = kind }));
            Assert.Contains($"data-pt=\"{kind} ", DesignCompiler.Compile(scene));
            Assert.DoesNotContain(DesignValidator.Validate(scene), i => i.Code == "particles");
        }
    }

    [Fact]
    public void No_particles_is_left_out_of_the_saved_shape()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new DesignShape { Kind = "rect" });
        Assert.DoesNotContain("particles", json);
    }
}
