using InvitesBlog.Application.Campaigns;
using InvitesBlog.Infrastructure.Images;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class ImageColourReaderTests
{
    /// <summary>A soft pink gradient, like a Canva card, with a band of rose lettering on about 2% of it.</summary>
    private static byte[] PinkCard(bool jpeg = false, bool lettering = true)
    {
        using var image = new Image<Rgba32>(400, 600);
        for (var y = 0; y < 600; y++)
        {
            var t = y / 599.0;
            var row = new Rgba32((byte)(251 - 8 * t), (byte)(227 - 45 * t), (byte)(234 - 34 * t));
            for (var x = 0; x < 400; x++) image[x, y] = row;
        }
        // Lettering as thin strokes rather than a block: what a typed name on a card really is.
        if (lettering)
            for (var line = 0; line < 3; line++)
                for (var y = 180 + line * 30; y < 183 + line * 30; y++)
                    for (var x = 50; x < 350; x++)
                        if ((x / 6) % 2 == 0) image[x, y] = new Rgba32(176, 64, 95);
        using var ms = new MemoryStream();
        // The upload step stores designs re-encoded, often as JPEG, whose noise scatters thin lettering.
        if (jpeg) image.Save(ms, new SixLabors.ImageSharp.Formats.Jpeg.JpegEncoder { Quality = 82 });
        else image.Save(ms, new PngEncoder());
        return ms.ToArray();
    }

    private static (int R, int G, int B) Rgb(string hex) =>
        (Convert.ToInt32(hex[1..3], 16), Convert.ToInt32(hex[3..5], 16), Convert.ToInt32(hex[5..7], 16));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Finds_the_lettering_as_the_vivid_colour(bool jpeg)
    {
        var read = new ImageSharpColourReader().Read(PinkCard(jpeg));

        Assert.NotNull(read?.Vivid);
        var (r, g, b) = Rgb(read!.Vivid!);
        Assert.True(Math.Abs(r - 176) < 30 && Math.Abs(g - 64) < 30 && Math.Abs(b - 95) < 30, $"vivid {read.Vivid}");
    }

    /// <summary>The whole path, picture to suggestion: the button is the lettering's rose, PNG or JPEG.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_pink_card_suggests_its_rose_for_the_button(bool jpeg)
    {
        var accent = GuestColours.Suggest(new ImageSharpColourReader().Read(PinkCard(jpeg)))[0].Colours.Accent;

        // The rose is #b0405f (red 112 above green); the grey-mauve this replaced was #877077 (23).
        var (r, g, _) = Rgb(accent);
        Assert.True(r - g > 70, $"accent {accent} is not the rose");
    }

    /// <summary>
    /// No lettering to go by, just the soft gradient: the button is a deeper pink of the same hue, not
    /// the grey-mauve that darkening toward black gave (what the first real upload got).
    /// </summary>
    [Fact]
    public void A_plain_pink_picture_gets_a_deeper_pink_not_grey()
    {
        var accent = GuestColours.Suggest(new ImageSharpColourReader().Read(PinkCard(jpeg: true, lettering: false)))[0].Colours.Accent;

        var (r, g, b) = Rgb(accent);
        var max = Math.Max(r, Math.Max(g, b)); var min = Math.Min(r, Math.Min(g, b));
        Assert.True((max - min) / (double)max > 0.45, $"accent {accent} is too grey");
        Assert.True(r > g && r > b, $"accent {accent} is not pink");
    }

    [Fact]
    public void Something_that_is_not_an_image_reads_as_nothing() =>
        Assert.Null(new ImageSharpColourReader().Read("not an image"u8.ToArray()));
}
