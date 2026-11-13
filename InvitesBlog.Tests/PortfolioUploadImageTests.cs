using System.Diagnostics;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Infrastructure.Images;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Metadata.Profiles.Exif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>Runs only where libheif's heif-dec is installed (the API image and the dev machine); skipped elsewhere.</summary>
public sealed class HeifFactAttribute : FactAttribute
{
    public HeifFactAttribute()
    {
        if (!HeifAvailable()) Skip = "heif-dec / heif-convert (libheif-examples) isn't installed here.";
    }

    internal static bool HeifAvailable() => Works("heif-dec") || Works("heif-convert");

    private static bool Works(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, "--version")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            if (p is null) return false;
            p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            return p.WaitForExit(10_000);
        }
        catch (System.ComponentModel.Win32Exception) { return false; }
    }
}

/// <summary>
/// Pictures uploaded straight into a portfolio (past work): web copies only, the right way up, and NOTHING the camera
/// wrote survives, above all the GPS position (owner, 2026-11-13: privacy).
/// </summary>
public class PortfolioUploadImageTests
{
    private static ImageSharpOptimizer Sut() => new(NullLogger<ImageSharpOptimizer>.Instance);

    internal static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "Fixtures", name);

    /// <summary>A camera JPEG stored sideways (tagged "rotate 90° clockwise"), with a GPS position and a camera name.</summary>
    internal static byte[] CameraJpeg(int storedWidth = 3000, int storedHeight = 2000, int quality = 95)
    {
        using var image = new Image<Rgba32>(storedWidth, storedHeight, new Rgba32(30, 30, 200));
        // The stored left edge is red: rotated for display, it becomes the top.
        for (var y = 0; y < storedHeight; y++)
            for (var x = 0; x < storedWidth / 10; x++) image[x, y] = new Rgba32(220, 20, 20);
        var exif = new ExifProfile();
        exif.SetValue(ExifTag.Orientation, (ushort)6);
        exif.SetValue(ExifTag.Make, "TestCam");
        exif.SetValue(ExifTag.GPSLatitudeRef, "N");
        exif.SetValue(ExifTag.GPSLatitude, [new Rational(4, 1), new Rational(10, 1), new Rational(30, 1)]);
        exif.SetValue(ExifTag.GPSLongitudeRef, "E");
        exif.SetValue(ExifTag.GPSLongitude, [new Rational(73, 1), new Rational(30, 1), new Rational(0, 1)]);
        image.Metadata.ExifProfile = exif;
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = quality });
        return ms.ToArray();
    }

    internal static void AssertNoMetadata(byte[] bytes)
    {
        var info = Image.Identify(bytes);
        Assert.NotNull(info);
        Assert.Null(info.Metadata.ExifProfile);
        Assert.Null(info.Metadata.XmpProfile);
        Assert.Null(info.Metadata.IptcProfile);
        // And not hiding anywhere in the file either.
        var text = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.DoesNotContain("Exif\0", text);
        Assert.DoesNotContain("TestCam", text);
    }

    [Fact]
    public void A_camera_photo_becomes_two_web_copies_the_right_way_up_with_no_gps_or_camera_notes()
    {
        var source = CameraJpeg();
        // The test's own input really carries the GPS position (so the next assertions mean something).
        Assert.NotNull(Image.Identify(source).Metadata.ExifProfile?.GetValue(ExifTag.GPSLatitude));

        var copies = Sut().MakeWebCopies(source, 2048, 1024, 80_000_000, out var problem);

        Assert.Equal(WebCopyProblem.None, problem);
        Assert.NotNull(copies);
        Assert.Equal("image/jpeg", copies.Full.ContentType);
        // 3000x2000 stored sideways = 2000x3000 upright, so 2048 tall and 1365 wide; the thumb 1024 tall.
        Assert.Equal((1365, 2048), (copies.Full.Width, copies.Full.Height));
        Assert.Equal((683, 1024), (copies.Thumb.Width, copies.Thumb.Height));
        foreach (var bytes in new[] { copies.Full.Content, copies.Thumb.Content })
        {
            AssertNoMetadata(bytes);
            using var img = Image.Load<Rgba32>(bytes);
            Assert.True(img[img.Width / 2, 3].R > 150, "the red edge should be at the top");
            Assert.True(img[img.Width / 2, img.Height - 3].B > 150, "blue at the bottom");
        }
    }

    [Fact]
    public void A_small_photo_is_never_passed_through_as_it_came_so_its_metadata_still_goes()
    {
        // Small and already well compressed: the display optimiser would hand these bytes back untouched.
        var source = CameraJpeg(300, 200, quality: 40);
        var copies = Sut().MakeWebCopies(source, 2048, 1024, 80_000_000, out _);
        Assert.NotNull(copies);
        Assert.Equal((200, 300), (copies.Full.Width, copies.Full.Height));
        AssertNoMetadata(copies.Full.Content);
        AssertNoMetadata(copies.Thumb.Content);
    }

    [Fact]
    public void A_transparent_png_stays_png_and_see_through_and_an_opaque_one_becomes_jpeg()
    {
        using var clear = new Image<Rgba32>(800, 600, new Rgba32(0, 0, 0, 0));
        for (var y = 100; y < 500; y++) for (var x = 100; x < 700; x++) clear[x, y] = new Rgba32(10, 200, 10, 255);
        using var ms = new MemoryStream();
        clear.SaveAsPng(ms);
        var copies = Sut().MakeWebCopies(ms.ToArray(), 2048, 1024, 80_000_000, out _);
        Assert.NotNull(copies);
        Assert.Equal("image/png", copies.Full.ContentType);
        using (var img = Image.Load<Rgba32>(copies.Full.Content)) Assert.Equal(0, img[0, 0].A);

        using var solid = new Image<Rgba32>(800, 600, new Rgba32(10, 200, 10, 255));
        using var ms2 = new MemoryStream();
        solid.SaveAsPng(ms2);
        Assert.Equal("image/jpeg", Sut().MakeWebCopies(ms2.ToArray(), 2048, 1024, 80_000_000, out _)!.Full.ContentType);
    }

    [Fact]
    public void Too_many_pixels_and_unreadable_bytes_are_refused_with_a_reason()
    {
        var big = CameraJpeg(1000, 1000);
        Assert.Null(Sut().MakeWebCopies(big, 2048, 1024, 999_999, out var tooMany));
        Assert.Equal(WebCopyProblem.TooManyPixels, tooMany);

        var broken = big.Take(400).ToArray();
        Assert.Null(Sut().MakeWebCopies(broken, 2048, 1024, 80_000_000, out var unreadable));
        Assert.Equal(WebCopyProblem.Unreadable, unreadable);
    }

    [HeifFact]
    public async Task An_iphone_heic_is_decoded_the_right_way_up_and_its_web_copy_loses_the_gps()
    {
        // 160x90 stored, with a 90° turn and EXIF carrying a GPS position (Fixtures/portrait-gps.heic, made with heif-enc
        // from a JPEG whose stored left half is red, right half blue).
        var heic = await File.ReadAllBytesAsync(Fixture("portrait-gps.heic"));
        Assert.Equal("image/heic", InvitesBlog.Application.Common.ImageSniffer.Detect(heic));
        var converter = new HeifDecConverter(new ConfigurationBuilder().Build(), NullLogger<HeifDecConverter>.Instance);

        var jpeg = await converter.ToJpegAsync(heic);

        Assert.NotNull(jpeg);
        var copies = Sut().MakeWebCopies(jpeg, 2048, 1024, 80_000_000, out _);
        Assert.NotNull(copies);
        Assert.Equal((90, 160), (copies.Full.Width, copies.Full.Height));
        AssertNoMetadata(copies.Full.Content);
        using var img = Image.Load<Rgba32>(copies.Full.Content);
        Assert.True(img[45, 5].R > 150 && img[45, 5].B < 100, "red on top");
        Assert.True(img[45, 155].B > 150 && img[45, 155].R < 100, "blue at the bottom");
    }

    [HeifFact]
    public async Task The_older_heif_convert_name_works_the_same_way()
    {
        // The API image (Ubuntu 24.04, libheif 1.17) only has heif-convert.
        var heic = await File.ReadAllBytesAsync(Fixture("portrait-gps.heic"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Images:HeifDec"] = "heif-convert" }).Build();
        var jpeg = await new HeifDecConverter(config, NullLogger<HeifDecConverter>.Instance).ToJpegAsync(heic);
        Assert.NotNull(jpeg);
        Assert.Equal((90, 160), (Image.Identify(jpeg).Width, Image.Identify(jpeg).Height));
    }

    [Fact]
    public async Task No_decoder_installed_means_nothing_comes_back_not_a_crash()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Images:HeifDec"] = "no-such-heif-tool" }).Build();
        Assert.Null(await new HeifDecConverter(config, NullLogger<HeifDecConverter>.Instance).ToJpegAsync([1, 2, 3]));
    }

    [HeifFact]
    public async Task Bytes_that_only_look_like_heic_come_back_as_nothing()
    {
        var heic = await File.ReadAllBytesAsync(Fixture("portrait-gps.heic"));
        var converter = new HeifDecConverter(new ConfigurationBuilder().Build(), NullLogger<HeifDecConverter>.Instance);
        Assert.Null(await converter.ToJpegAsync(heic.Take(40).ToArray()));
    }
}
