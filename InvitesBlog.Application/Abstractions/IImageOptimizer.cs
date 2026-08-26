namespace InvitesBlog.Application.Abstractions;

/// <summary>
/// Edge caps a caller can ask for when it knows how small a slot is actually rendered. This is policy,
/// not capability: the optimizer decides how to shrink, the caller decides how much is worth keeping.
/// </summary>
public static class ImageEdgeCaps
{
    /// <summary>
    /// Longest edge for an image the template shows as one of many small prints.
    /// <para>
    /// The slots these land in are genuinely small. A gallery print in Gilded Hour is painted about
    /// 249x280 CSS pixels even while it is lifted to the front, so 512 is roughly a 2x screen's worth
    /// and about 60% of what a 3x phone would ask for. Measured against the originals at the size a
    /// print is actually painted, that costs 1.3% mean difference versus 0.55% at 1024 — a little
    /// softer if you go looking on the held photo, and not something you see in the fan.
    /// </para>
    /// <para>
    /// What it buys is the memory the browser has to hold to draw six of them at once: 49 MB as
    /// uploaded, 18 MB at 1024, 4.8 MB here. That is the number that makes the section stutter on a
    /// phone, and it scales with pixels, not with file size.
    /// </para>
    /// </summary>
    public const int Gallery = 512;
}

/// <summary>The result of running an upload through the optimizer.</summary>
/// <param name="Content">The bytes to store — the original when nothing was worth changing.</param>
/// <param name="ContentType">Unchanged from the input: the optimizer never switches format.</param>
/// <param name="Width">Pixel width after any resize, or 0 when the image was passed through.</param>
/// <param name="Height">Pixel height after any resize, or 0 when the image was passed through.</param>
/// <param name="Changed">True when <paramref name="Content"/> differs from what was handed in.</param>
public sealed record OptimizedImage(
    byte[] Content, string ContentType, int Width, int Height, bool Changed);

/// <summary>
/// Shrinks an uploaded photo to something a browser can actually render.
/// <para>
/// Guests upload straight off a phone, where a single photo is routinely 10&#8211;20 MB and 4000px
/// wide. Six of those in one invitation is well over a hundred megabytes for the browser to decode
/// and hold as bitmaps at once, which is what made the page hang. Nobody uploading a photo has a
/// way to resize it first, so the server has to.
/// </para>
/// <para>
/// It is deliberately conservative: it never changes format, never upscales, and hands back the
/// original untouched whenever re-encoding would not actually help or the format cannot be
/// safely rewritten. A failure to decode is never a failed upload.
/// </para>
/// </summary>
public interface IImageOptimizer
{
    /// <param name="maxEdge">
    /// Longest edge to keep, overriding the default. Pass a smaller cap for images the template only
    /// ever renders small — a gallery print is a couple of hundred CSS pixels wide, so storing it at
    /// full cover resolution buys nothing visible and costs the browser a much larger bitmap.
    /// </param>
    OptimizedImage Optimize(byte[] content, string contentType, int? maxEdge = null);

    /// <summary>
    /// Every pixel as uploaded, with the camera metadata removed. For the one thing we store that is
    /// somebody's own photograph rather than an image a template renders: an event photo is a keepsake
    /// people download afterwards, so shrinking it is a loss rather than an optimisation.
    ///
    /// <para>The metadata still goes. EXIF carries GPS, and these are photographs OF other people's
    /// guests — a location tag would publish where someone's wedding was to anyone who saves a picture
    /// of it. Orientation is baked in by the decoder first, so dropping the rest costs nothing
    /// visible.</para>
    ///
    /// <para>Like <see cref="Optimize"/> it is conservative: same format, and the original bytes come
    /// back untouched whenever the file cannot be safely rewritten. A failure to decode is never a
    /// failed upload.</para>
    /// </summary>
    OptimizedImage Preserve(byte[] content, string contentType);
}
