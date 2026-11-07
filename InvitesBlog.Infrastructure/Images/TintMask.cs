using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;

namespace InvitesBlog.Infrastructure.Images;

/// <summary>
/// What the designer needs to tint a picture, worked out once rather than on every frame.
///
/// <para>A picture with see-through pixels is tinted through its own alpha (only what has colour is
/// painted), so all it needs to know is that it has them. An opaque one (a JPG ornament) gets a
/// <b>brightness mask</b>: a PNG whose alpha says how much of each pixel the tint paints. The
/// background, judged from the picture's edges, becomes see-through and whatever differs from it takes
/// the colour: white lace on black keeps the lace (the dark areas go), and black lace on white keeps
/// the lace too (the light areas go). Brightness is stretched between the background and the far end
/// of the picture, and the faintest differences (JPEG noise round the background) are dropped.</para>
/// </summary>
public static class TintMask
{
    /// <summary>Below this share of full strength a pixel is background noise and goes see-through.</summary>
    public const double Floor = 0.08;

    /// <summary>Whether any pixel is see-through; null when the picture can't be read.</summary>
    public static bool? HasAlpha(byte[] content)
    {
        try
        {
            using var image = Image.Load<Rgba32>(content);
            var alpha = false;
            image.ProcessPixelRows(rows =>
            {
                for (var y = 0; y < rows.Height && !alpha; y++)
                    foreach (var p in rows.GetRowSpan(y))
                        if (p.A < 250) { alpha = true; break; }
            });
            return alpha;
        }
        catch (Exception e) when (e is NotSupportedException or InvalidImageContentException or UnknownImageFormatException)
        {
            return null;
        }
    }

    public static (bool Alpha, byte[]? Mask) Analyse(byte[] content)
    {
        using var image = Image.Load<Rgba32>(content);
        int w = image.Width, h = image.Height;
        var lum = new byte[w * h];
        var alpha = false;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < w; x++)
                {
                    var p = row[x];
                    if (p.A < 250) alpha = true;
                    lum[y * w + x] = (byte)Math.Round(0.2126 * p.R + 0.7152 * p.G + 0.0722 * p.B);
                }
            }
        });
        if (alpha) return (true, null);

        // The background: the middle brightness of the picture's outer edge.
        var edge = new List<byte>(2 * (w + h));
        for (var x = 0; x < w; x++) { edge.Add(lum[x]); edge.Add(lum[(h - 1) * w + x]); }
        for (var y = 0; y < h; y++) { edge.Add(lum[y * w]); edge.Add(lum[y * w + w - 1]); }
        edge.Sort();
        double background = edge[edge.Count / 2];
        var lightBackground = background >= 128;

        // The far end: the 2nd percentile on a light background, the 98th on a dark one, so a few
        // stray pixels don't set the scale.
        var counts = new int[256];
        foreach (var l in lum) counts[l]++;
        double Percentile(double q)
        {
            var target = q * lum.Length;
            var seen = 0;
            for (var v = 0; v < 256; v++) { seen += counts[v]; if (seen >= target) return v; }
            return 255;
        }
        var far = lightBackground ? Percentile(0.02) : Percentile(0.98);
        var range = Math.Max(16, Math.Abs(background - far));

        using var mask = new Image<La16>(w, h);
        mask.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < h; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < w; x++)
                {
                    var l = lum[y * w + x];
                    var a = Math.Clamp((lightBackground ? background - l : l - background) / range, 0, 1);
                    if (a < Floor) a = 0;
                    row[x] = new La16(0, (byte)Math.Round(a * 255));
                }
            }
        });
        using var ms = new MemoryStream();
        mask.Save(ms, new PngEncoder { ColorType = PngColorType.GrayscaleWithAlpha, BitDepth = PngBitDepth.Bit8 });
        return (false, ms.ToArray());
    }
}
