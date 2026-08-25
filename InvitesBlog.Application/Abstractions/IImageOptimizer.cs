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
    /// A gallery print in Gilded Hour renders about 180&#8211;260 CSS pixels wide, so even a 3x phone
    /// asks for roughly 640 real pixels at full lift. Six stored at the pipeline's default came to
    /// 49 MB of bitmap held at once, which is what made the fan stutter on a phone; at 1024 the same
    /// six are about 17 MB. Nothing on screen changes &#8212; there were never enough pixels on the
    /// display to show the difference. Kept well above the measured need so a tablet, or a future
    /// layout that shows these larger, still has room.
    /// </para>
    /// </summary>
    public const int Gallery = 1024;
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
}
