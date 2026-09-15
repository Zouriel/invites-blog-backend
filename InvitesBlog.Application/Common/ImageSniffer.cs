using System.Text;

namespace InvitesBlog.Application.Common;

/// <summary>
/// Decides whether an uploaded "image" is really a photograph, from its bytes.
///
/// <para><b>Why this exists.</b> Everything people upload is served back from the app's own origin,
/// where the session token lives. An SVG is not a picture in that position, it is a document: opened
/// directly, its script runs with the session in reach. The declared content type cannot be the
/// judge — it is whatever the client said — so the first bytes are. A file only passes when they
/// are a raster format we recognise, whatever it was labelled.</para>
///
/// <para><b>Sniffed, not decoded.</b> The formats accepted include HEIC and AVIF, which the image
/// library here cannot decode but which are exactly what a phone camera produces, and which are inert
/// either way. Recognising the container is enough to rule out markup, which is the point.</para>
/// </summary>
public static class ImageSniffer
{
    /// <summary>What an uploader is told when a file is refused.</summary>
    public const string Refusal = "That file isn't a photo we can use. Upload a JPEG, PNG, GIF, WebP or HEIC image.";

    /// <summary>The error code that goes with <see cref="Refusal"/>.</summary>
    public const string RefusalCode = "unsupported_image";

    private static ReadOnlySpan<byte> PngSignature => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>
    /// True when the bytes are a raster image AND the label is not a markup type. Both halves: a PNG
    /// label on SVG bytes is refused by the first, and an SVG label on PNG bytes by the second, because
    /// the label is what the file will be served as.
    /// </summary>
    public static bool IsPhoto(ReadOnlySpan<byte> content, string? declaredType)
    {
        var type = (declaredType ?? string.Empty).Trim().ToLowerInvariant();
        if (type.Contains("svg", StringComparison.Ordinal) || type.Contains("xml", StringComparison.Ordinal)
            || type.Contains("html", StringComparison.Ordinal))
            return false;

        return Detect(content) is not null;
    }

    /// <summary>The raster format the bytes open with, or null when they are not one we accept.</summary>
    public static string? Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF) return "image/jpeg";
        if (b.StartsWith(PngSignature)) return "image/png";
        if (b.StartsWith("GIF87a"u8) || b.StartsWith("GIF89a"u8)) return "image/gif";
        if (b.Length >= 12 && b[..4].SequenceEqual("RIFF"u8) && b.Slice(8, 4).SequenceEqual("WEBP"u8))
            return "image/webp";

        // ISO base media: a size, then "ftyp", then the major brand. HEIC and AVIF both live here.
        if (b.Length >= 12 && b.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            return Encoding.ASCII.GetString(b.Slice(8, 4)) switch
            {
                "avif" or "avis" => "image/avif",
                "heic" or "heix" or "hevc" or "hevx" or "heim" or "heis" or "mif1" or "msf1" => "image/heic",
                _ => null,
            };
        }

        return null;
    }
}
