using InvitesBlog.Application.Abstractions;
using InvitesBlog.Infrastructure.Images;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Six phone photos in one invitation used to hang the page — 4000px each, decoded to bitmaps the
/// browser holds all at once. These pin the shrink down and, just as importantly, pin down when it
/// must keep its hands off: a failed or pointless optimisation must never cost someone their upload.
/// </summary>
public class ImageOptimizerTests
{
    private static ImageSharpOptimizer Sut() => new(NullLogger<ImageSharpOptimizer>.Instance);

    /// <summary>A photo-like image: noise, so it can't be compressed away to nothing.</summary>
    private static byte[] Photo(int width, int height, string format = "jpeg", int quality = 100)
    {
        using var image = new Image<Rgba32>(width, height);
        var rng = new Random(1);
        image.Mutate(ctx => ctx.ProcessPixelRowsAsVector4(row =>
        {
            for (var x = 0; x < row.Length; x++)
                row[x] = new System.Numerics.Vector4(
                    (float)rng.NextDouble(), (float)rng.NextDouble(), (float)rng.NextDouble(), 1f);
        }));

        using var ms = new MemoryStream();
        if (format == "png") image.Save(ms, new PngEncoder());
        else image.Save(ms, new JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    private static (int Width, int Height) DimensionsOf(byte[] bytes)
    {
        using var img = Image.Load(bytes);
        return (img.Width, img.Height);
    }

    // ----- the actual job -----

    [Fact]
    public void A_phone_sized_photo_is_shrunk_to_the_max_edge()
    {
        var original = Photo(4032, 3024);            // a stock 12MP phone photo

        var result = Sut().Optimize(original, "image/jpeg");

        Assert.True(result.Changed);
        Assert.Equal(ImageSharpOptimizer.MaxEdge, result.Width);
        Assert.True(result.Content.Length < original.Length);
        Assert.Equal("image/jpeg", result.ContentType);   // format never changes
    }

    [Fact]
    public void Shrinking_keeps_the_aspect_ratio()
    {
        var original = Photo(4000, 2000);

        var result = Sut().Optimize(original, "image/jpeg");

        var (w, h) = DimensionsOf(result.Content);
        Assert.Equal(ImageSharpOptimizer.MaxEdge, w);
        Assert.Equal(ImageSharpOptimizer.MaxEdge / 2, h);
    }

    [Fact]
    public void A_portrait_photo_is_bounded_by_its_height()
    {
        var original = Photo(3024, 4032);

        var result = Sut().Optimize(original, "image/jpeg");

        var (w, h) = DimensionsOf(result.Content);
        Assert.Equal(ImageSharpOptimizer.MaxEdge, h);
        Assert.True(w < h);
    }

    [Fact]
    public void Six_phone_photos_together_come_down_to_something_a_browser_can_hold()
    {
        // The reported failure: six of these at once. Guarding the aggregate, not just one file.
        var originals = Enumerable.Range(0, 6).Select(_ => Photo(4032, 3024)).ToList();

        var optimized = originals.Select(o => Sut().Optimize(o, "image/jpeg").Content).ToList();

        var before = originals.Sum(o => (long)o.Length);
        var after = optimized.Sum(o => (long)o.Length);
        Assert.True(after < before / 2, $"expected a big cut, got {before / 1024} KB -> {after / 1024} KB");
    }

    [Fact]
    public void A_png_stays_a_png()
    {
        var original = Photo(4000, 3000, "png");

        var result = Sut().Optimize(original, "image/png");

        Assert.True(result.Changed);
        Assert.Equal("image/png", result.ContentType);
        using var img = Image.Load(result.Content);   // still decodable, still a real image
        Assert.Equal(ImageSharpOptimizer.MaxEdge, img.Width);
    }

    [Fact]
    public void Transparency_survives_the_shrink()
    {
        // A fully transparent image: Image<Rgba32> starts zeroed, which is transparent black.
        using var source = new Image<Rgba32>(3000, 3000);
        using var ms = new MemoryStream();
        source.Save(ms, new PngEncoder());

        var result = Sut().Optimize(ms.ToArray(), "image/png");

        using var img = Image.Load<Rgba32>(result.Content);
        Assert.Equal(0, img[0, 0].A);   // still transparent, not flattened onto black
    }

    [Fact]
    public void A_gallery_print_is_capped_smaller_than_a_cover()
    {
        // A gallery print renders a couple of hundred CSS pixels wide, so cover resolution is bitmap
        // the browser can never show. Six at the default came to 49 MB held at once and made the fan
        // stutter on a phone.
        var original = Photo(4032, 3024);

        var result = Sut().Optimize(original, "image/jpeg", ImageEdgeCaps.Gallery);

        Assert.Equal(ImageEdgeCaps.Gallery, result.Width);
        Assert.True(ImageEdgeCaps.Gallery < ImageSharpOptimizer.MaxEdge);
    }

    [Fact]
    public void Six_gallery_prints_come_down_to_a_fraction_of_the_bitmap()
    {
        // The number that actually matters is decoded pixels held at once, not the download.
        var originals = Enumerable.Range(0, 6).Select(_ => Photo(2048, 1536)).ToList();

        var atCover = originals.Select(o => DimensionsOf(Sut().Optimize(o, "image/jpeg").Content)).ToList();
        var atGallery = originals
            .Select(o => DimensionsOf(Sut().Optimize(o, "image/jpeg", ImageEdgeCaps.Gallery).Content)).ToList();

        long Bitmap(List<(int Width, int Height)> d) => d.Sum(x => (long)x.Width * x.Height * 4);
        Assert.True(Bitmap(atGallery) * 3 < Bitmap(atCover),
            $"expected a big cut in held bitmap, got {Bitmap(atCover) / 1_000_000} MB -> {Bitmap(atGallery) / 1_000_000} MB");
    }

    [Fact]
    public void A_slot_cannot_ask_for_more_pixels_than_the_pipeline_allows()
    {
        // The cap is a floor-lowering knob only; nothing gets to opt out of the pipeline's ceiling.
        var original = Photo(4032, 3024);

        var result = Sut().Optimize(original, "image/jpeg", 8000);

        Assert.Equal(ImageSharpOptimizer.MaxEdge, result.Width);
    }

    [Fact]
    public void A_photo_already_under_the_gallery_cap_is_not_upscaled()
    {
        // Sized off the cap rather than a literal, so lowering the cap can't quietly turn this into a
        // test that the photo IS resized — which is what happened when it moved from 1024 to 512.
        var w0 = ImageEdgeCaps.Gallery / 2;
        var h0 = ImageEdgeCaps.Gallery / 4;
        var original = Photo(w0, h0, quality: 70);

        var result = Sut().Optimize(original, "image/jpeg", ImageEdgeCaps.Gallery);

        var (w, h) = DimensionsOf(result.Changed ? result.Content : original);
        Assert.Equal(w0, w);
        Assert.Equal(h0, h);
    }

    // ----- when it must keep its hands off -----

    [Fact]
    public void A_small_already_compressed_photo_is_left_alone()
    {
        // Re-encoding this would only add a second generation of JPEG loss for no size win, so the
        // optimizer keeps the original bytes. Under the max edge AND no smaller once re-encoded.
        var original = Photo(800, 600, quality: 70);

        var result = Sut().Optimize(original, "image/jpeg");

        Assert.False(result.Changed);
        Assert.Same(original, result.Content);
    }

    [Fact]
    public void A_small_but_wastefully_encoded_photo_is_still_compressed()
    {
        // Small enough not to need resizing, but saved at near-lossless quality — the kind of export
        // that is several megabytes for no visible benefit. Worth re-encoding.
        var original = Photo(800, 600, quality: 100);

        var result = Sut().Optimize(original, "image/jpeg");

        Assert.True(result.Changed);
        Assert.True(result.Content.Length < original.Length);
        var (w, h) = DimensionsOf(result.Content);
        Assert.Equal(800, w);      // not resized, just re-compressed
        Assert.Equal(600, h);
    }

    [Theory]
    [InlineData("image/svg+xml")]   // vector — resizing is meaningless
    [InlineData("image/gif")]       // rewriting drops the animation
    [InlineData("image/avif")]      // this ImageSharp line cannot decode it
    public void Formats_that_must_not_be_rewritten_pass_straight_through(string contentType)
    {
        var original = Photo(4000, 3000);

        var result = Sut().Optimize(original, contentType);

        Assert.False(result.Changed);
        Assert.Same(original, result.Content);
    }

    [Fact]
    public void Undecodable_bytes_do_not_fail_the_upload()
    {
        // A corrupt or mislabelled file still gets stored — optimisation is a nicety, not a gate.
        var junk = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        var result = Sut().Optimize(junk, "image/jpeg");

        Assert.False(result.Changed);
        Assert.Same(junk, result.Content);
    }

    [Fact]
    public void Exif_is_stripped_from_what_gets_stored()
    {
        // Phone photos carry GPS. Publishing an invitation shouldn't publish where a photo was taken.
        using var source = Image.Load(Photo(3000, 2000));
        source.Metadata.ExifProfile = new SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifProfile();
        source.Metadata.ExifProfile.SetValue(
            SixLabors.ImageSharp.Metadata.Profiles.Exif.ExifTag.Copyright, "somewhere private");
        using var ms = new MemoryStream();
        source.Save(ms, new JpegEncoder { Quality = 100 });

        var result = Sut().Optimize(ms.ToArray(), "image/jpeg");

        using var stored = Image.Load(result.Content);
        Assert.Null(stored.Metadata.ExifProfile);
    }
}
