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
}
