using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;

namespace InvitesBlog.Infrastructure.Images;

/// <summary>
/// A design picture as WebP for "Shrink to fit", see-through parts kept. A painted or photographed
/// picture (many colours: watercolour flowers, a photo) is saved at high quality (90): on the owner's
/// wreath that was 84 KB against a 329 KB PNG with no visible difference, an average change of about
/// 1/255 per colour. A flat graphic (few colours: lettering, a logo, simple shapes) is saved lossless,
/// pixel for pixel, because that is where lossy edges would show.
/// </summary>
public static class WebpShrink
{
    /// <summary>More distinct colours than this and it's a painted or photographed picture.</summary>
    public const int FlatColours = 1024;
    public const int Quality = 90;

    /// <summary>The picture as WebP, or null when it can't be read. The caller keeps whichever is smaller.</summary>
    public static (byte[] Content, bool Lossless)? Encode(byte[] content)
    {
        try
        {
            using var image = Image.Load<Rgba32>(content);
            var lossless = IsFlat(image);
            using var output = new MemoryStream();
            image.Save(output, lossless
                ? new WebpEncoder { FileFormat = WebpFileFormatType.Lossless, Quality = 100, Method = WebpEncodingMethod.BestQuality }
                : new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = Quality, Method = WebpEncodingMethod.BestQuality });
            return (output.ToArray(), lossless);
        }
        catch (Exception e) when (e is NotSupportedException or InvalidImageContentException or UnknownImageFormatException)
        {
            return null;
        }
    }

    /// <summary>Few distinct colours (counting see-through as one): a flat graphic.</summary>
    public static bool IsFlat(Image<Rgba32> image)
    {
        var colours = new HashSet<uint>();
        var flat = true;
        image.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height && flat; y++)
                foreach (var p in rows.GetRowSpan(y))
                {
                    colours.Add(p.A == 0 ? 0u : p.PackedValue);
                    if (colours.Count > FlatColours) { flat = false; break; }
                }
        });
        return flat;
    }
}
