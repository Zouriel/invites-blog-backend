using System.Text;
using InvitesBlog.Application.Common;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// What passes as a photograph. The label a client puts on a file is whatever it chose, so the bytes
/// decide — and markup of any kind never passes, because it is served back on the app's own origin.
/// </summary>
public class ImageSnifferTests
{
    private static byte[] Encoded(SixLabors.ImageSharp.Formats.IImageEncoder encoder)
    {
        using var image = new Image<Rgba32>(8, 8);
        using var ms = new MemoryStream();
        image.Save(ms, encoder);
        return ms.ToArray();
    }

    public static TheoryData<string, byte[]> Rasters => new()
    {
        { "image/jpeg", Encoded(new JpegEncoder()) },
        { "image/png", Encoded(new PngEncoder()) },
        { "image/gif", Encoded(new GifEncoder()) },
        { "image/webp", Encoded(new WebpEncoder()) },
    };

    [Theory]
    [MemberData(nameof(Rasters))]
    public void Real_raster_images_pass(string type, byte[] bytes)
    {
        Assert.Equal(type, ImageSniffer.Detect(bytes));
        Assert.True(ImageSniffer.IsPhoto(bytes, type));
    }

    /// <summary>What an iPhone produces. The image library here cannot decode it, and it is inert.</summary>
    [Theory]
    [InlineData("heic", "image/heic")]
    [InlineData("mif1", "image/heic")]
    [InlineData("avif", "image/avif")]
    public void Phone_formats_are_recognised_by_their_container(string brand, string expected)
    {
        byte[] header = [0, 0, 0, 24, .. "ftyp"u8.ToArray(), .. Encoding.ASCII.GetBytes(brand), 0, 0, 0, 0];

        Assert.Equal(expected, ImageSniffer.Detect(header));
    }

    [Theory]
    [InlineData("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>")]
    [InlineData("<?xml version=\"1.0\"?><svg onload=\"alert(1)\"/>")]
    [InlineData("<!doctype html><script>alert(1)</script>")]
    [InlineData("   <svg/>")]
    public void Markup_never_passes_whatever_the_label(string markup)
    {
        var bytes = Encoding.UTF8.GetBytes(markup);

        Assert.False(ImageSniffer.IsPhoto(bytes, "image/png"));
        Assert.False(ImageSniffer.IsPhoto(bytes, "image/jpeg"));
        Assert.False(ImageSniffer.IsPhoto(bytes, "image/svg+xml"));
    }

    /// <summary>The label is what the file is served as, so a markup label is refused even on real bytes.</summary>
    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/xhtml+xml")]
    public void A_markup_label_is_refused_even_on_real_image_bytes(string label) =>
        Assert.False(ImageSniffer.IsPhoto(Encoded(new PngEncoder()), label));

    [Fact]
    public void Unknown_or_empty_bytes_do_not_pass()
    {
        Assert.False(ImageSniffer.IsPhoto([], "image/png"));
        Assert.False(ImageSniffer.IsPhoto([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12], "image/png"));
        // An ISO media file that is a video, not a picture, is not accepted as one.
        byte[] mp4 = [0, 0, 0, 24, .. "ftyp"u8.ToArray(), .. "isom"u8.ToArray(), 0, 0, 0, 0];
        Assert.False(ImageSniffer.IsPhoto(mp4, "image/png"));
    }
}
