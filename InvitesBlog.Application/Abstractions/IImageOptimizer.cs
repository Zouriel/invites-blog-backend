namespace InvitesBlog.Application.Abstractions;

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
    OptimizedImage Optimize(byte[] content, string contentType);
}
