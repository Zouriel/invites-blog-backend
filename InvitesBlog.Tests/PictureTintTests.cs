using System.Text.RegularExpressions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Infrastructure.Images;
using InvitesBlog.TemplateCompiler.Design;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Tinting pictures. A PNG with see-through parts becomes a silhouette through its own alpha, so only
/// what has colour is painted; an opaque JPG is a silhouette through its brightness mask, or keeps the
/// photo with the colour multiplied over it.
/// </summary>
public class PictureTintTests
{
    /// <summary>A 40x40 ornament: a filled square in the middle, see-through (or plain background) round it.</summary>
    private static byte[] Ornament(bool transparent, bool lightBackground = true, string format = "png")
    {
        using var image = new Image<Rgba32>(40, 40);
        var bg = transparent ? new Rgba32(0, 0, 0, 0) : lightBackground ? new Rgba32(250, 250, 248) : new Rgba32(8, 8, 10);
        var ink = lightBackground ? new Rgba32(30, 20, 20) : new Rgba32(240, 236, 230);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < 40; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < 40; x++) row[x] = x is >= 10 and < 30 && y is >= 10 and < 30 ? ink : bg;
            }
        });
        // A soft, half see-through edge on the transparent one.
        if (transparent) image[9, 20] = new Rgba32(30, 20, 20, 128);
        using var ms = new MemoryStream();
        if (format == "jpeg") image.SaveAsJpeg(ms, new JpegEncoder { Quality = 95 });
        else image.SaveAsPng(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static string Uri(byte[] bytes, string type = "png") => $"data:image/{type};base64,{Convert.ToBase64String(bytes)}";

    // ----- What tinting needs, worked out once -----

    [Fact]
    public void A_png_with_see_through_parts_needs_no_mask()
    {
        Assert.Equal((true, (byte[]?)null), TintMask.Analyse(Ornament(transparent: true)));
        Assert.True(TintMask.HasAlpha(Ornament(transparent: true)));
        Assert.False(TintMask.HasAlpha(Ornament(transparent: false)));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void An_opaque_jpg_gets_a_mask_where_the_background_goes_and_the_ornament_stays(bool lightBackground)
    {
        var (alpha, mask) = TintMask.Analyse(Ornament(transparent: false, lightBackground, "jpeg"));
        Assert.False(alpha);
        using var m = Image.Load<La16>(mask!);
        Assert.Equal((40, 40), (m.Width, m.Height));
        Assert.Equal(0, m[2, 2].A);       // background: see-through
        Assert.Equal(0, m[37, 20].A);
        Assert.True(m[20, 20].A > 240);   // the ornament: painted
    }

    // ----- Rendering -----

    private static DesignScene Scene(DesignAsset asset, DesignImage image, bool inGroup = false)
    {
        var scene = DesignStarters.Create("blank")!;
        scene.Stage = false;
        scene.Assets["pic"] = asset;
        var el = new DesignElement
        {
            Id = "pic", Type = "image", X = 40, Y = 120, W = 200, H = 200, Image = image,
            // A way in, a loop and a slow zoom (ken-burns) on the same element.
            Keyframes = [new() { T = 0, Opacity = 0, Scale = 1 }, new() { T = 0.2, Opacity = 1 }, new() { T = 1, Scale = 1.18 }],
            Loop = new DesignLoop { Repeat = 2, Frames = [new() { T = 0.5, Dy = -6 }] },
        };
        scene.Elements.Add(inGroup ? new DesignElement { Id = "grp", Type = "group", X = 0, Y = 0, W = 390, H = 400, Children = [el] } : el);
        return scene;
    }

    private static DesignAsset Png() => new() { Kind = "image", Data = Uri(Ornament(transparent: true)), Width = 40, Height = 40, Alpha = true };
    private static DesignAsset Jpg(bool mask = true) => new()
    {
        Kind = "image", Data = Uri(Ornament(false, true, "jpeg"), "jpeg"), Width = 40, Height = 40, Alpha = false,
        Mask = mask ? Uri(TintMask.Analyse(Ornament(false, true, "jpeg")).Mask!) : null,
    };

    private static string Rule(string html) => Regex.Match(html, @"\.e\d+ \.i0\{[^}]*\}").Value;

    [Fact]
    public void A_png_with_see_through_parts_is_painted_only_where_it_has_colour()
    {
        var html = DesignCompiler.Compile(Scene(Png(), new DesignImage { Asset = "pic", Fit = "contain", Tint = "#AA0000" }));
        // The picture's bytes once, as a variable the mask reads too.
        Assert.Matches(@"\.i0\{--im:url\(""data:image/png;base64,[^""]+""\);background-image:var\(--im\)\}", html);
        Assert.Single(Regex.Matches(html, "data:image/png;base64,"));
        // The box is filled with the colour and cut to the picture's alpha: see-through stays see-through.
        Assert.EndsWith(";background-image:none;background-color:#aa0000;-webkit-mask:var(--im) center/contain no-repeat;mask:var(--im) center/contain no-repeat}", Rule(html));
    }

    [Fact]
    public void An_opaque_jpg_is_a_silhouette_through_its_brightness_mask()
    {
        var html = DesignCompiler.Compile(Scene(Jpg(), new DesignImage { Asset = "pic", Fit = "cover", Tint = "#AA0000" }));
        Assert.Matches(@"\.k0\{--mk:url\(""data:image/png;base64,[^""]+""\)\}", html);
        Assert.Contains("<div class=\"i0 k0\" role=\"presentation\">", html);
        Assert.EndsWith(";background-image:none;background-color:#aa0000;-webkit-mask:var(--mk) center/cover no-repeat;mask:var(--mk) center/cover no-repeat}", Rule(html));
    }

    [Fact]
    public void An_opaque_jpg_in_overlay_keeps_the_photo_with_the_colour_multiplied_over_it()
    {
        var html = DesignCompiler.Compile(Scene(Jpg(), new DesignImage { Asset = "pic", Fit = "contain", Tint = "#AA0000", TintMode = "overlay" }));
        Assert.DoesNotContain(".k0{", html);
        // Cut to the photo's own box, so a fitted photo's empty sides stay empty.
        Assert.EndsWith(";background-color:#aa0000;background-blend-mode:multiply;-webkit-mask:var(--im) center/contain no-repeat;mask:var(--im) center/contain no-repeat}", Rule(html));
        // With no mask worked out yet, a silhouette falls back to the overlay rather than a solid block.
        var noMask = DesignCompiler.Compile(Scene(Jpg(mask: false), new DesignImage { Asset = "pic", Tint = "#AA0000" }));
        Assert.Contains("background-blend-mode:multiply", Rule(noMask));
    }

    [Fact]
    public void A_theme_tint_follows_the_theme()
    {
        var html = DesignCompiler.Compile(Scene(Png(), new DesignImage { Asset = "pic", Tint = "theme:accent" }));
        Assert.Contains(";background-color:var(--ib-accent);", Rule(html));
    }

    [Fact]
    public void Cleared_the_picture_compiles_exactly_as_it_did_before_tints()
    {
        var html = DesignCompiler.Compile(Scene(Jpg(), new DesignImage { Asset = "pic", Fit = "cover", Tint = null, TintMode = "overlay" }));
        Assert.Matches(@"\.i0\{background-image:url\(""data:image/jpeg;base64,[^""]+""\)\}", html);
        Assert.DoesNotContain("--im", html);
        Assert.DoesNotContain(".k0", html);
        Assert.Equal(".e" + Regex.Match(Rule(html), @"\.e(\d+)").Groups[1].Value + " .i0{width:100%;height:100%;background-repeat:no-repeat;background-position:center;background-size:cover}", Rule(html));
    }

    [Fact]
    public void A_tinted_picture_inside_a_group_is_tinted()
    {
        var html = DesignCompiler.Compile(Scene(Png(), new DesignImage { Asset = "pic", Tint = "#AA0000" }, inGroup: true));
        Assert.Contains("mask:var(--im)", Rule(html));
        Assert.DoesNotContain(DesignValidator.Validate(Scene(Png(), new DesignImage { Asset = "pic", Tint = "#AA0000" }, inGroup: true)), i => i.Severity == DesignIssue.Error);
    }

    [Fact]
    public void A_tint_changes_only_the_picture_so_motion_compiles_as_before()
    {
        string Motion(string html) => string.Join("\n", html.Split('}').Where(r => r.Contains("animation") || r.Contains("@keyframes")));
        var plain = DesignCompiler.Compile(Scene(Png(), new DesignImage { Asset = "pic" }));
        var tinted = DesignCompiler.Compile(Scene(Png(), new DesignImage { Asset = "pic", Tint = "theme:accent" }));
        Assert.NotEmpty(Motion(plain));
        Assert.Equal(Motion(plain), Motion(tinted));
        // The mask is on the picture's own box, inside the element the motion moves and scales.
        Assert.Matches(@"<div class=""i0"" role=""presentation""></div>", tinted);
    }

    [Fact]
    public void A_stretched_picture_fills_its_box_tinted_or_not()
    {
        Assert.EndsWith("background-size:100% 100%}", Rule(DesignCompiler.Compile(Scene(Png(), new DesignImage { Asset = "pic", Fit = "stretch" }))));
        Assert.Contains("mask:var(--im) center/100% 100% no-repeat", Rule(DesignCompiler.Compile(Scene(Png(), new DesignImage { Asset = "pic", Fit = "stretch", Tint = "#AA0000" }))));
    }

    [Fact]
    public void Check_refuses_a_tint_that_is_not_a_colour_or_a_mode_that_does_not_exist()
    {
        var issues = DesignValidator.Validate(Scene(Jpg(), new DesignImage { Asset = "pic", Tint = "pinkish", TintMode = "sepia" }));
        Assert.Contains(issues, i => i.Severity == DesignIssue.Error && i.Code == "color");
        Assert.Contains(issues, i => i.Severity == DesignIssue.Error && i.Code == "tint_mode");
        var badMask = Jpg();
        badMask.Mask = "data:text/html;base64,PGI+";
        Assert.Contains(DesignValidator.Validate(Scene(badMask, new DesignImage { Asset = "pic" })), i => i.Code == "image_invalid");
    }
}
