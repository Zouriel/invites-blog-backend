using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Logging;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.Processing;

namespace InvitesBlog.Infrastructure.Images;

/// <inheritdoc cref="IImageOptimizer"/>
public sealed class ImageSharpOptimizer(ILogger<ImageSharpOptimizer> logger) : IImageOptimizer
{
    /// <summary>
    /// Longest edge kept.
    /// <para>
    /// Chosen on the browser's decode cost, not the file size. A 4032x3024 phone photo is ~46 MB as a
    /// bitmap; six of them is ~279 MB held at once, which is what hung the page. At 2048 that falls
    /// to ~12 MB each, ~72 MB for six. Still more pixels than a 3x phone screen can show, and
    /// indistinguishable under object-fit on a laptop.
    /// </para>
    /// </summary>
    public const int MaxEdge = 2048;


    /// <summary>High enough that the difference is invisible at these dimensions, well below the
    /// near-lossless quality phones encode at (which is where most of the file size goes).</summary>
    private const int JpegQuality = 82;
    private const int WebpQuality = 82;

    public OptimizedImage Optimize(byte[] content, string contentType, int? maxEdge = null)
    {
        var type = (contentType ?? string.Empty).ToLowerInvariant();
        // A caller may cap smaller than the default, never larger — an image is not worth storing at
        // more pixels than the pipeline's own ceiling just because a slot asked for it.
        var edge = maxEdge is > 0 && maxEdge < MaxEdge ? maxEdge.Value : MaxEdge;

        // SVG is vector — resizing is meaningless and re-encoding would rasterise it. GIF and AVIF
        // are passed through rather than mangled: rewriting a GIF drops its animation, and this
        // ImageSharp line cannot decode AVIF at all.
        if (type is "image/svg+xml" or "image/gif" or "image/avif")
            return Passthrough(content, contentType);

        try
        {
            using var image = Image.Load(content);

            var longest = Math.Max(image.Width, image.Height);
            var needsResize = longest > edge;
            if (needsResize)
            {
                var scale = (double)edge / longest;
                image.Mutate(x => x.Resize(new ResizeOptions
                {
                    // Never upscale, and keep the aspect ratio — Max fits inside the box.
                    Mode = ResizeMode.Max,
                    Size = new Size(
                        Math.Max(1, (int)Math.Round(image.Width * scale)),
                        Math.Max(1, (int)Math.Round(image.Height * scale))),
                    Sampler = KnownResamplers.Lanczos3,
                }));
            }

            // Camera EXIF carries orientation, GPS and thumbnails. Orientation is already baked in by
            // the decoder, so dropping the rest costs nothing and avoids publishing where a photo was
            // taken. Done for every image, resized or not.
            image.Metadata.ExifProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.XmpProfile = null;

            using var output = new MemoryStream();
            var encoded = Encode(image, output, type);
            if (!encoded) return Passthrough(content, contentType);

            var bytes = output.ToArray();

            // Re-encoding can end up LARGER than the original — an already-optimised export, or a
            // photographic PNG. Only keep the new bytes when they actually win; a resize always
            // wins on the browser's decode cost even if the file is a wash, so that still counts.
            if (!needsResize && bytes.Length >= content.Length)
                return Passthrough(content, contentType);

            logger.LogInformation(
                "Image optimised: {OldKb} KB -> {NewKb} KB ({W}x{H}{Resized}).",
                content.Length / 1024, bytes.Length / 1024, image.Width, image.Height,
                needsResize ? ", resized" : "");

            return new OptimizedImage(bytes, contentType, image.Width, image.Height, true);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidImageContentException or ImageFormatException)
        {
            // Something we cannot read. The upload itself is still fine — store what we were given.
            logger.LogWarning(ex, "Could not optimise an uploaded image ({ContentType}); storing it unchanged.", contentType);
            return Passthrough(content, contentType);
        }
    }

    /// <inheritdoc />
    public OptimizedImage Preserve(byte[] content, string contentType)
    {
        var type = (contentType ?? string.Empty).ToLowerInvariant();

        // Same formats the resize path refuses to rewrite: SVG is vector, a re-encoded GIF loses its
        // animation, and this ImageSharp line cannot decode AVIF at all. Passing them through means
        // their metadata survives — accepted, because mangling somebody's photograph is worse, and
        // none of the three is what a phone camera produces.
        if (type is "image/svg+xml" or "image/gif" or "image/avif")
            return Passthrough(content, contentType);

        try
        {
            using var image = Image.Load(content);

            // No resize. This is the whole difference from Optimize: an event photo is the picture
            // somebody took, and the copy they may want back is the one they took.
            image.Metadata.ExifProfile = null;
            image.Metadata.IptcProfile = null;
            image.Metadata.XmpProfile = null;

            using var output = new MemoryStream();
            if (!Encode(image, output, type, PngCompressionLevel.DefaultCompression))
                return Passthrough(content, contentType);

            var bytes = output.ToArray();

            // Re-encoding at full resolution can easily come out LARGER than a camera's own encode.
            // Nothing was resized, so if the bytes grew there is nothing to gain by keeping them —
            // except the stripped metadata, which is the point. Keep the smaller file only when the
            // metadata was not the reason we were here.
            var hadMetadata = bytes.Length < content.Length;
            logger.LogInformation(
                "Event photo kept at full size: {OldKb} KB -> {NewKb} KB ({W}x{H}, metadata stripped).",
                content.Length / 1024, bytes.Length / 1024, image.Width, image.Height);

            return new OptimizedImage(bytes, contentType, image.Width, image.Height, hadMetadata);
        }
        catch (Exception ex) when (ex is NotSupportedException or InvalidImageContentException or ImageFormatException)
        {
            logger.LogWarning(ex, "Could not re-encode an uploaded photo ({ContentType}); storing it unchanged.", contentType);
            return Passthrough(content, contentType);
        }
    }

    /// <summary>Writes the image back in the SAME format it arrived as. Changing it would invalidate
    /// the content type and extension already chosen by the caller.</summary>
    /// <param name="png">
    /// How hard to compress a PNG. <see cref="PngCompressionLevel.BestCompression"/> is right for the
    /// images a template renders — paid once, and every guest pays the download. It is the wrong trade
    /// for a full-resolution event-photo original: measured on a 4000x3000 PNG it cost about twelve
    /// seconds of a guest's upload for a few percent of a file almost nobody downloads.
    /// </param>
    private static bool Encode(
        Image image, Stream output, string contentType,
        PngCompressionLevel png = PngCompressionLevel.BestCompression)
    {
        switch (contentType)
        {
            case "image/jpeg":
                image.Save(output, new JpegEncoder { Quality = JpegQuality });
                return true;
            case "image/png":
                image.Save(output, new PngEncoder { CompressionLevel = png });
                return true;
            case "image/webp":
                image.Save(output, new WebpEncoder { Quality = WebpQuality });
                return true;
            default:
                // A content type we did not expect. We could round-trip it in whatever format the
                // decoder recognised, but the stored content type would then be a lie — safer to skip.
                return false;
        }
    }

    private static OptimizedImage Passthrough(byte[] content, string contentType) =>
        new(content, contentType, 0, 0, false);
}
