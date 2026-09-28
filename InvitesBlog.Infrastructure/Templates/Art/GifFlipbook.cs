using InvitesBlog.Application.Exceptions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.Formats.Webp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace InvitesBlog.Infrastructure.Templates.Art;

/// <summary>
/// An animated GIF as a handful of stills, for a flipbook the scroll plays. The page can't carry the
/// GIF itself as scroll motion — a GIF plays on its own clock — so a few frames at even moments are
/// kept, shrunk and re-encoded as WebP until together they fit an invitation's size budget.
/// </summary>
public static class GifFlipbook
{
    /// <summary>Decoded pixels allowed in memory at once (every frame is decoded): ~256 MB of RGBA.</summary>
    private const long MaxDecodedPixels = 64L * 1024 * 1024;

    public sealed record Frame(byte[] Webp, int Width, int Height);

    /// <param name="Seconds">One play-through of the GIF.</param>
    public sealed record Result(IReadOnlyList<Frame> Frames, int Width, int Height, double Seconds);

    /// <summary>Frame count and size from the file's blocks, without decoding anything. Null when it isn't a GIF.</summary>
    public static (int Width, int Height, int Frames)? Scan(byte[] gif)
    {
        if (gif.Length < 13 || gif[0] != 'G' || gif[1] != 'I' || gif[2] != 'F') return null;
        var width = gif[6] | gif[7] << 8;
        var height = gif[8] | gif[9] << 8;
        var i = 13;
        if ((gif[10] & 0x80) != 0) i += 3 * (1 << ((gif[10] & 7) + 1));
        var frames = 0;
        while (i < gif.Length)
        {
            var block = gif[i++];
            if (block == 0x3B) break;
            if (block == 0x21)
            {
                i++; // label
                i = SkipSubBlocks(gif, i);
            }
            else if (block == 0x2C)
            {
                if (i + 9 > gif.Length) break;
                var flags = gif[i + 8];
                i += 9;
                if ((flags & 0x80) != 0) i += 3 * (1 << ((flags & 7) + 1));
                i++; // LZW minimum code size
                i = SkipSubBlocks(gif, i);
                frames++;
            }
            else break;
        }
        return (width, height, frames);
    }

    private static int SkipSubBlocks(byte[] data, int i)
    {
        while (i < data.Length)
        {
            var size = data[i++];
            if (size == 0) break;
            i += size;
        }
        return i;
    }

    /// <summary>
    /// Up to <paramref name="maxFrames"/> stills at even moments of one play-through, together under
    /// <paramref name="budgetBytes"/>. Null for a GIF that doesn't move (one frame).
    /// </summary>
    public static Result? Build(byte[] gif, int maxFrames, int budgetBytes)
    {
        var scan = Scan(gif) ?? throw new BusinessRuleException("That GIF couldn't be read.", "image_invalid");
        if (scan.Frames <= 1) return null;
        if ((long)scan.Width * scan.Height * scan.Frames > MaxDecodedPixels)
            throw new BusinessRuleException("That animation is too long or too large to turn into an invitation — try a smaller one.", "art_too_large");

        using var image = Image.Load<Rgba32>(gif);
        var count = image.Frames.Count;
        var delays = new double[count];
        for (var k = 0; k < count; k++)
        {
            var cs = image.Frames[k].Metadata.GetGifMetadata().FrameDelay;
            delays[k] = (cs <= 1 ? 10 : cs) / 100.0; // browsers treat 0–1 as 0.1s
        }
        var total = delays.Sum();

        for (var frames = Math.Min(maxFrames, count); frames >= 2; frames = frames > 6 ? frames - 2 : frames - 1)
        {
            // The frame on screen at each even moment.
            var picks = new List<int>();
            for (var j = 0; j < frames; j++)
            {
                var at = total * j / frames;
                double t = 0;
                var index = 0;
                while (index < count - 1 && t + delays[index] <= at) t += delays[index++];
                picks.Add(index);
            }
            foreach (var edge in new[] { 360, 300, 240, 180 })
            {
                var output = new List<Frame>();
                var bytes = 0;
                foreach (var index in picks)
                {
                    using var still = image.Frames.CloneFrame(index);
                    // A GIF's comments, XMP and colour profile come along with every frame otherwise — tens of KB each.
                    still.Metadata.ExifProfile = null;
                    still.Metadata.XmpProfile = null;
                    still.Metadata.IccProfile = null;
                    still.Metadata.IptcProfile = null;
                    still.Frames.RootFrame.Metadata.XmpProfile = null;
                    still.Frames.RootFrame.Metadata.IccProfile = null;
                    still.Frames.RootFrame.Metadata.ExifProfile = null;
                    var longest = Math.Max(still.Width, still.Height);
                    if (longest > edge)
                    {
                        var scale = (double)edge / longest;
                        still.Mutate(x => x.Resize(Math.Max(1, (int)Math.Round(still.Width * scale)), Math.Max(1, (int)Math.Round(still.Height * scale))));
                    }
                    using var ms = new MemoryStream();
                    still.Save(ms, new WebpEncoder { FileFormat = WebpFileFormatType.Lossy, Quality = 70, UseAlphaCompression = true });
                    output.Add(new Frame(ms.ToArray(), still.Width, still.Height));
                    bytes += (int)ms.Length;
                    if (bytes > budgetBytes) break;
                }
                if (bytes <= budgetBytes) return new Result(output, scan.Width, scan.Height, total);
            }
        }
        throw new BusinessRuleException("That animation is too detailed to fit in an invitation, even shrunk — try a simpler one.", "art_too_large");
    }
}
