using InvitesBlog.Infrastructure.Images;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Which way up a photograph comes out.
///
/// <para>A phone writes the sensor's pixels unrotated and records the rotation in an EXIF tag. Every
/// bit of metadata is stripped here — a GPS tag on a photograph of somebody's wedding guests is not
/// ours to republish — so the rotation has to be APPLIED before the tag carrying it is dropped, or
/// the picture is simply on its side.</para>
///
/// <para>This was invisible for as long as every capture came through a canvas, whose output has no
/// EXIF and whose pixels are already the right way up. It appeared the moment anything handed over
/// an untouched camera file — and it was never only about the camera: a portrait chosen from a
/// phone's library arrives the same way.</para>
/// </summary>
public class ImageOrientationTests
{
    private static ImageSharpOptimizer Sut() => new(NullLogger<ImageSharpOptimizer>.Instance);

    /// <summary>
    /// A landscape image tagged "rotate 90° clockwise to display" — what a phone held upright
    /// produces. Displayed correctly it is TALLER than it is wide.
    /// </summary>
    private static byte[] SidewaysPortrait(int storedWidth = 400, int storedHeight = 200)
    {
        using var image = new Image<Rgba32>(storedWidth, storedHeight);
        // Mark one edge so the rotation can be checked by where the colour ends up, not just by shape.
        for (var y = 0; y < storedHeight; y++) image[0, y] = new Rgba32(255, 0, 0);

        image.Metadata.ExifProfile = new ExifProfile();
        image.Metadata.ExifProfile.SetValue(ExifTag.Orientation, (ushort)6);   // rotate 90° CW

        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = 95 });
        return ms.ToArray();
    }

    [Fact]
    public void The_original_is_kept_the_right_way_up()
    {
        var result = Sut().Preserve(SidewaysPortrait(), "image/jpeg");

        using var output = Image.Load(result.Content);
        Assert.True(
            output.Height > output.Width,
            $"a portrait came back {output.Width}x{output.Height} — the rotation was dropped, not applied");
    }

    [Fact]
    public void An_image_kept_as_it_was_still_reports_its_size()
    {
        // A small, already well-compressed JPEG: re-encoding can't beat it, so the original bytes are
        // kept. Its size must still come back — the designer lays the picture out by it.
        using var image = new Image<Rgba32>(300, 120);
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = 30 });

        var result = Sut().Optimize(ms.ToArray(), "image/jpeg", 1600);

        Assert.Equal(ms.ToArray(), result.Content);
        Assert.Equal((300, 120), (result.Width, result.Height));
    }

    [Fact]
    public void A_resized_copy_is_kept_the_right_way_up()
    {
        var result = Sut().Optimize(SidewaysPortrait(2000, 1000), "image/jpeg", 800);

        using var output = Image.Load(result.Content);
        Assert.True(
            output.Height > output.Width,
            $"a portrait came back {output.Width}x{output.Height} — the rotation was dropped, not applied");
    }

    /// <summary>
    /// Applying the rotation must not be a reason to keep the rest. These are photographs of other
    /// people's guests, and the location one was taken is not ours to hand on.
    /// </summary>
    [Fact]
    public void Nothing_else_from_the_camera_survives()
    {
        var result = Sut().Preserve(SidewaysPortrait(), "image/jpeg");

        using var output = Image.Load(result.Content);
        Assert.Null(output.Metadata.ExifProfile);
        Assert.Null(output.Metadata.IptcProfile);
        Assert.Null(output.Metadata.XmpProfile);
    }

    /// <summary>An image with nothing to correct is left as it is.</summary>
    [Fact]
    public void An_untagged_image_is_not_turned()
    {
        using var plain = new Image<Rgba32>(400, 200);
        using var ms = new MemoryStream();
        plain.Save(ms, new JpegEncoder { Quality = 95 });

        var result = Sut().Preserve(ms.ToArray(), "image/jpeg");

        using var output = Image.Load(result.Content);
        Assert.True(output.Width > output.Height, "an untagged landscape was rotated anyway");
    }

    private static byte[] Shot(int w = 400, int h = 300, Action<ExifProfile>? exif = null, int quality = 98)
    {
        using var image = new Image<Rgb24>(w, h);
        // Enough detail that a second encode would show.
        image.ProcessPixelRows(a =>
        {
            for (var y = 0; y < a.Height; y++)
            {
                var row = a.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++) row[x] = new Rgb24((byte)(x * 37 ^ y * 11), (byte)(x * y), (byte)(y * 53 ^ x));
            }
        });
        if (exif != null)
        {
            image.Metadata.ExifProfile = new ExifProfile();
            exif(image.Metadata.ExifProfile);
        }
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    private static byte[] PictureData(byte[] jpeg)
    {
        for (var i = 2; i < jpeg.Length - 1; i++)
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xDB) return jpeg[i..];
        throw new InvalidOperationException("no tables");
    }

    /// <summary>What was wrong in production: every "original" came back at web quality.</summary>
    [Fact]
    public void A_shot_the_right_way_up_keeps_every_byte_of_its_picture()
    {
        var shot = Shot(exif: e => e.SetValue(ExifTag.Orientation, (ushort)1));

        var result = Sut().Preserve(shot, "image/jpeg");

        Assert.Equal(PictureData(shot), PictureData(result.Content));
        Assert.Equal((400, 300), (result.Width, result.Height));
    }

    [Fact]
    public void A_kept_shot_loses_its_location_and_notes()
    {
        var shot = Shot(exif: e =>
        {
            e.SetValue(ExifTag.Software, "SecretCamera");
            e.SetValue(ExifTag.GPSLatitudeRef, "N");
        });

        var result = Sut().Preserve(shot, "image/jpeg");

        using var output = Image.Load(result.Content);
        Assert.Null(output.Metadata.ExifProfile);
        Assert.DoesNotContain("SecretCamera", System.Text.Encoding.Latin1.GetString(result.Content));
    }

    [Fact]
    public void Anything_hung_on_after_the_picture_goes_too()
    {
        var shot = Shot();
        var withTail = shot.Concat(new byte[] { 0xFF, 0xD8, 1, 2, 3, 0xFF, 0xD9 }).ToArray();

        var result = Sut().Preserve(withTail, "image/jpeg");

        Assert.Equal(shot.Length, result.Content.Length);
    }

    [Fact]
    public void A_shot_that_has_to_turn_is_encoded_far_better_than_web_quality()
    {
        var turned = Shot(400, 300, e => e.SetValue(ExifTag.Orientation, (ushort)6));

        var kept = Sut().Preserve(turned, "image/jpeg");

        using var output = Image.Load(kept.Content);
        Assert.Equal((300, 400), (output.Width, output.Height));
        var web = Sut().Optimize(turned, "image/jpeg", 4000);
        Assert.True(kept.Content.Length > web.Content.Length * 1.5, $"{kept.Content.Length} against {web.Content.Length}");
    }

    [Fact]
    public void A_broken_file_is_still_stored_rather_than_refused()
    {
        var shot = Shot();
        var broken = shot[..(shot.Length / 2)];

        var result = Sut().Preserve(broken, "image/jpeg");

        Assert.NotEmpty(result.Content);
    }
}
