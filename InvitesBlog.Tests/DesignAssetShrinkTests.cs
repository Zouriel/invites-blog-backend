using System.Text;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Infrastructure.Images;
using InvitesBlog.Infrastructure.Templates;
using InvitesBlog.TemplateCompiler.Design;
using InvitesBlog.TemplateCompiler.Design.Art;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>"Shrink to fit": an asset made as small as it can be for the largest size the design shows it at.</summary>
public class DesignAssetShrinkTests
{
    private static DesignEngine Engine() => new(new RawTemplatePackager(Substitute.For<IStorageService>()),
        new ImageSharpOptimizer(NullLogger<ImageSharpOptimizer>.Instance),
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Urls:AssetsBase"] = "/assets" }).Build());

    /// <summary>A photo-like picture: every pixel different, so it doesn't compress to nothing.</summary>
    private static string Png(int w, int h, bool transparent = false)
    {
        using var image = new Image<Rgba32>(w, h);
        var rnd = new Random(7);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32((byte)(x * 255 / w), (byte)(y * 255 / h), (byte)rnd.Next(256), transparent && x < w / 2 ? (byte)0 : (byte)255);
            }
        });
        using var ms = new MemoryStream();
        image.Save(ms, new PngEncoder());
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    private static (int W, int H, string Type) Decode(string data)
    {
        var bytes = Convert.FromBase64String(data[(data.IndexOf(',') + 1)..]);
        using var image = Image.Load(bytes);
        return (image.Width, image.Height, data[5..data.IndexOf(';')]);
    }

    [Fact]
    public void A_picture_is_resized_to_twice_the_size_it_is_shown_at()
    {
        var data = Png(1600, 1200);
        var r = Engine().ShrinkAsset(new("a1", "image", data, 200, 150, "photo"));
        Assert.Equal((400, 300), (r.Width, r.Height));
        // A painted or photographed picture (many colours) is saved as high-quality WebP.
        Assert.Equal((400, 300, "image/webp"), Decode(r.Data));
        Assert.True(r.Data.Length < data.Length / 5);
        Assert.Equal(("a1", "photo"), (r.Id, r.Name));
    }

    [Fact]
    public void A_picture_with_see_through_parts_keeps_them()
    {
        var r = Engine().ShrinkAsset(new("a1", "image", Png(1200, 800, transparent: true), 150, 100, null));
        Assert.Equal((300, 200, "image/webp"), Decode(r.Data));
        Assert.True(r.Alpha);
        using var image = Image.Load<Rgba32>(Convert.FromBase64String(r.Data[(r.Data.IndexOf(',') + 1)..]));
        Assert.Equal(0, image[10, 100].A);
    }

    [Fact]
    public void A_picture_already_small_for_its_size_keeps_every_pixel_and_never_grows()
    {
        var data = Png(300, 200);
        var r = Engine().ShrinkAsset(new("a1", "image", data, 300, 200, null));
        Assert.Equal((300, 200), (Decode(r.Data).W, Decode(r.Data).H));
        Assert.True(r.Data.Length <= data.Length);
    }

    [Fact]
    public void A_picture_is_never_made_bigger_than_it_is()
    {
        var data = Png(120, 80);
        var r = Engine().ShrinkAsset(new("a1", "image", data, 900, 600, null));
        Assert.Equal((120, 80), (Decode(r.Data).W, Decode(r.Data).H));
    }

    [Fact]
    public void An_animated_gif_is_left_alone()
    {
        // GIF89a, 1x1: the optimizer never rewrites a GIF (it would lose its animation).
        var gif = "data:image/gif;base64,R0lGODlhAQABAIAAAAAAAP///yH5BAEAAAAALAAAAAABAAEAAAIBRAA7";
        Assert.Equal(gif, Engine().ShrinkAsset(new("a1", "image", gif, 10, 10, null)).Data);
    }

    [Fact]
    public void A_flat_graphic_is_saved_lossless_so_every_pixel_is_the_same()
    {
        // Lettering-like: three colours with hard edges, where lossy compression would smudge.
        using var image = new Image<Rgba32>(600, 240, new Rgba32(0, 0, 0, 0));
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    if ((x / 24 + y / 24) % 3 == 0) row[x] = new Rgba32(140, 20, 60);
                    else if (x is > 300 and < 560 && y is > 40 and < 200) row[x] = new Rgba32(250, 220, 120);
            }
        });
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        var data = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
        Assert.True(WebpShrink.IsFlat(image));
        var r = Engine().ShrinkAsset(new("a1", "image", data, 300, 120, null)); // shown half size: resized to 600 wide, i.e. kept
        Assert.Equal("image/webp", Decode(r.Data).Type);
        using var back = Image.Load<Rgba32>(Convert.FromBase64String(r.Data[(r.Data.IndexOf(',') + 1)..]));
        for (var y = 0; y < 240; y++)
            for (var x = 0; x < 600; x++)
                if (image[x, y].A > 0 || back[x, y].A > 0) Assert.Equal(image[x, y], back[x, y]);
    }

    [Fact]
    public void A_painted_picture_as_webp_looks_the_same_on_average()
    {
        // A smooth gradient with soft noise, like watercolour: lossy WebP at 90 changes it by very little.
        using var image = new Image<Rgba32>(400, 300);
        var rnd = new Random(3);
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                    row[x] = new Rgba32((byte)(120 + x / 4 + rnd.Next(6)), (byte)(80 + y / 3 + rnd.Next(6)), (byte)(160 + rnd.Next(6)));
            }
        });
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        var r = Engine().ShrinkAsset(new("a1", "image", "data:image/png;base64," + Convert.ToBase64String(ms.ToArray()), 400, 300, null));
        Assert.Equal("image/webp", Decode(r.Data).Type);
        using var back = Image.Load<Rgba32>(Convert.FromBase64String(r.Data[(r.Data.IndexOf(',') + 1)..]));
        double total = 0;
        for (var y = 0; y < 300; y++)
            for (var x = 0; x < 400; x++)
                total += Math.Abs(image[x, y].R - back[x, y].R) + Math.Abs(image[x, y].G - back[x, y].G) + Math.Abs(image[x, y].B - back[x, y].B);
        Assert.True(total / (400 * 300 * 3) < 3, $"average change {total / (400 * 300 * 3):F2}/255");
    }

    private const string Art = """
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 1000 1000">
          <path d="M 100.123456 200.654321 L 400.987654 600.123456 C 500.111111 700.222222 800.333333 900.444444 950.555555 120.666666 Z" fill="#c0a060"/>
          <circle cx="500.123456" cy="500.654321" r="80.333333" fill="#112233" stroke="#445566" stroke-width="3.141592"/>
          <rect x="10.5" y="10.5" width="100.25" height="50.125" fill="none" stroke="#aabbcc" stroke-width="1.5" vector-effect="non-scaling-stroke"/>
        </svg>
        """;

    [Fact]
    public void An_illustration_drops_the_precision_its_size_cannot_show()
    {
        var before = SvgSanitizer.Sanitize(Art);
        // Shown 100px wide: ten drawing units to a pixel, so whole numbers are already finer than a tenth of one.
        var r = Engine().ShrinkAsset(new("s1", "svg", before.Document, 100, 100, "roses"));
        var after = SvgSanitizer.Sanitize(r.Data);
        Assert.True(r.Bytes < Encoding.UTF8.GetByteCount(before.Document));
        Assert.Equal(before.Colors, after.Colors); // the per-colour choices and tints still line up
        Assert.Equal(before.ViewBox, after.ViewBox);
        Assert.Contains("cx=\"500\"", r.Data);
        Assert.Contains("r=\"80\"", r.Data);
        Assert.DoesNotContain(".123456", r.Data);
        // A stroke drawn in screen pixels keeps its width.
        Assert.Contains("stroke-width=\"1.5\"", r.Data);
    }

    [Fact]
    public void Shown_large_an_illustration_keeps_more_decimals()
    {
        var r = Engine().ShrinkAsset(new("s1", "svg", SvgSanitizer.Sanitize(Art).Document, 20000, 20000, null));
        Assert.Contains("cx=\"500.12\"", r.Data);
    }

    [Theory]
    [InlineData(10, 0)]
    [InlineData(1, 1)]
    [InlineData(0.24, 2)]
    [InlineData(0.01, 3)]
    [InlineData(0.00001, 4)]
    public void The_decimals_kept_hold_every_point_within_a_tenth_of_a_pixel(double unitsPerPx, int expected)
    {
        var d = SvgShrink.DecimalsFor(unitsPerPx);
        Assert.Equal(expected, d);
        Assert.True(d == 4 || 0.5 * Math.Pow(10, -d) / unitsPerPx <= SvgShrink.TolerancePx + 1e-12);
        // And no more than needed: one fewer would move a point further than that.
        if (d > 0) Assert.True(0.5 * Math.Pow(10, -(d - 1)) / unitsPerPx > SvgShrink.TolerancePx);
    }

    [Theory]
    [InlineData(0, 100)]
    [InlineData(double.NaN, 100)]
    [InlineData(100, 50000)]
    public void A_size_that_is_not_a_size_is_refused(double w, double h) =>
        Assert.Throws<BusinessRuleException>(() => Engine().ShrinkAsset(new("a1", "image", Png(10, 10), w, h, null)));

    [Fact]
    public void Data_that_is_not_a_picture_is_refused() =>
        Assert.Throws<BusinessRuleException>(() => Engine().ShrinkAsset(new("a1", "image", "data:text/html;base64,PGI+", 10, 10, null)));
}
