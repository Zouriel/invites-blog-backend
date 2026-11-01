using System.Text;

namespace InvitesBlog.Application.Common;

/// <summary>What a video's first bytes say it is, and how ffmpeg must read and write that family.</summary>
/// <param name="Demuxer">Named to ffmpeg outright, so it never guesses from the contents: a "video" that is really a playlist must not get as far as being followed.</param>
public sealed record VideoKind(string ContentType, string Extension, string Demuxer, string Muxer);

/// <summary>
/// Decides whether an uploaded "video" really is one, from its bytes. The same idea as
/// <see cref="ImageSniffer"/>: the label is whatever the client said. Recognising the container is all
/// this does; whether it can be played is ffprobe's question, asked later.
/// </summary>
public static class VideoSniffer
{
    public const string Refusal = "That file isn't a video we can use. Upload an MP4, MOV or WebM.";
    public const string RefusalCode = "unsupported_video";

    /// <summary>Enough of the file to answer: an EBML header names its doctype within this.</summary>
    public const int HeadBytes = 4096;

    private static readonly VideoKind Mp4 = new("video/mp4", ".mp4", "mov", "mp4");
    private static readonly VideoKind QuickTime = new("video/quicktime", ".mov", "mov", "mov");
    private static readonly VideoKind WebM = new("video/webm", ".webm", "matroska", "webm");
    private static readonly VideoKind Matroska = new("video/x-matroska", ".mkv", "matroska", "matroska");

    public static VideoKind? Detect(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 12 && b.Slice(4, 4).SequenceEqual("ftyp"u8))
        {
            // The same box holds HEIC and AVIF stills, which are photographs.
            if (ImageSniffer.Detect(b) is not null) return null;
            return b.Slice(8, 4).SequenceEqual("qt  "u8) ? QuickTime : Mp4;
        }

        // A QuickTime file with no ftyp opens straight on one of its ordinary boxes.
        if (b.Length >= 8)
        {
            var box = b.Slice(4, 4);
            if (box.SequenceEqual("moov"u8) || box.SequenceEqual("mdat"u8) || box.SequenceEqual("wide"u8)
                || box.SequenceEqual("free"u8) || box.SequenceEqual("skip"u8))
                return QuickTime;
        }

        // EBML, the header of WebM and Matroska alike. The doctype inside says which.
        if (b.Length >= 4 && b[0] == 0x1A && b[1] == 0x45 && b[2] == 0xDF && b[3] == 0xA3)
            return b.IndexOf("webm"u8) >= 0 ? WebM : Matroska;

        return null;
    }

    public static async Task<VideoKind?> DetectFileAsync(string path, CancellationToken ct = default)
    {
        await using var file = File.OpenRead(path);
        var head = new byte[HeadBytes];
        var read = await file.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, ct);
        return Detect(head.AsSpan(0, read));
    }
}

/// <summary>The size limits on a video, in one place.</summary>
public static class MediaLimits
{
    public const long Mb = 1024L * 1024;

    /// <summary>The largest video an album takes, whichever way it arrives.</summary>
    public const long MaxVideoBytes = 256 * Mb;

    /// <summary>
    /// The most that can safely go through Cloudflare on the old route. It refuses a body over 100 MB
    /// before the request ever reaches us (seen on the live site: 90 MB arrived, 110 MB got its 413),
    /// and the form and the still travel in the same body.
    /// </summary>
    public const long MaxThroughCloudflareBytes = 90 * Mb;

    public const string TooLargeCode = "video_too_large";
    public const string NeedsDirectCode = "video_needs_direct";

    public static string TooLarge(long bytes) =>
        $"This video is {bytes / Mb} MB and the most we can take is {MaxVideoBytes / Mb} MB. Try a shorter clip.";

    public static string NeedsDirect(long bytes) =>
        $"This video is {bytes / Mb} MB. Right now we can only take videos up to {MaxThroughCloudflareBytes / Mb} MB, " +
        "because the way we send larger ones is switched off for the moment. Please try again later, or send a shorter clip.";
}
