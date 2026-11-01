using InvitesBlog.Application.Common;

namespace InvitesBlog.Application.Abstractions;

/// <summary>What ffprobe says of a video. <see cref="Width"/> and <see cref="Height"/> are as displayed, after any rotation.</summary>
public sealed record VideoProbe(
    string Codec, int Width, int Height, double Seconds, long BitRate, double Fps, bool HasAudio, bool IsHdr);

public enum VideoCompressOutcome { Done, Failed, TimedOut }

/// <summary>
/// ffmpeg, as this app uses it. Every run names its demuxer, allows only local files, gets an empty
/// environment and a low priority: the input is an untrusted file on a server shared with other projects.
/// </summary>
public interface IVideoTools
{
    /// <summary>False when ffmpeg isn't there; nothing else here may be called then.</summary>
    bool Available { get; }

    /// <summary>A new, empty folder for one job's files. The caller deletes it.</summary>
    string NewWorkDir();

    /// <summary>Leftovers from a run that was stopped: folders older than this process, never its own.</summary>
    void ClearWorkDir();

    /// <summary>Whether the working disk has this much free and a safety margin on top.</summary>
    bool HasRoomFor(long bytes);

    /// <summary>Null when it isn't a video that can be read.</summary>
    Task<VideoProbe?> ProbeAsync(string path, VideoKind kind, CancellationToken ct = default);

    /// <summary>The same picture and sound in a new file with the camera's metadata gone (GPS, device, time). No re-encoding.</summary>
    Task<bool> StripMetadataAsync(string input, string output, VideoKind kind, CancellationToken ct = default);

    /// <summary>One frame as a JPEG, upright, or null if none can be drawn.</summary>
    Task<byte[]?> PosterAsync(string input, VideoKind kind, CancellationToken ct = default);

    /// <summary>H.264 + AAC in an MP4, no larger than 1080p and with the metadata gone.</summary>
    Task<VideoCompressOutcome> CompressAsync(string input, string output, VideoKind kind, CancellationToken ct = default);

    /// <summary>Whether the start and the end of the file decode, which a probe alone can't tell.</summary>
    Task<bool> PlaysAsync(string path, VideoKind kind, double seconds, CancellationToken ct = default);
}

/// <summary>The decisions about trimming, apart from running anything.</summary>
public static class VideoTrimRules
{
    /// <summary>The longest edge a trimmed video keeps: 1080p either way up.</summary>
    public const int MaxLongEdge = 1920;

    /// <summary>Below this there is little to gain and a file this size is no burden to anyone.</summary>
    public const long SmallEnoughBytes = 20 * MediaLimits.Mb;

    /// <summary>
    /// Bits per pixel per frame. An encoder at the quality we use lands around here, so a file already at
    /// or under it wouldn't shrink by enough to be worth the CPU or a generation of loss.
    /// </summary>
    public const double EfficientBitsPerPixel = 0.07;

    /// <summary>A new file has to save at least this share of the old one's size to replace it.</summary>
    public const double MinSaving = 0.10;

    private static readonly HashSet<string> Efficient = ["h264", "hevc", "av1", "vp9"];

    /// <summary>Null when it should be compressed, otherwise why not.</summary>
    public static string? WhyNotCompress(VideoProbe probe, long bytes)
    {
        // Without tone mapping an 8-bit H.264 copy of HDR comes out washed out, and these are keepsakes.
        if (probe.IsHdr) return "hdr";
        if (bytes < SmallEnoughBytes) return "already small";
        if (Efficient.Contains(probe.Codec) && Math.Max(probe.Width, probe.Height) <= MaxLongEdge
            && probe.Fps > 0 && probe.Width > 0 && probe.Height > 0
            && probe.BitRate / (probe.Width * (double)probe.Height * probe.Fps) <= EfficientBitsPerPixel)
            return "already efficient";
        return null;
    }

    public static bool WorthKeeping(long original, long trimmed) => trimmed <= original * (1 - MinSaving);

    /// <summary>Whether a trimmed copy is the same video: same shape, same length, sound kept, nothing larger than asked.</summary>
    public static bool Matches(VideoProbe original, VideoProbe trimmed)
    {
        if (trimmed.Codec != "h264" || trimmed.Width <= 0 || trimmed.Height <= 0) return false;
        if (Math.Max(trimmed.Width, trimmed.Height) > MaxLongEdge) return false;
        if (Math.Max(trimmed.Width, trimmed.Height) > Math.Max(original.Width, original.Height) + 1) return false;
        if (original.HasAudio && !trimmed.HasAudio) return false;
        if (original.Width > 0 && original.Height > 0
            && Math.Abs(trimmed.Width / (double)trimmed.Height - original.Width / (double)original.Height) > 0.02)
            return false;
        // A browser's recording often states no length at all, and then there is nothing to compare.
        return original.Seconds <= 0 || Math.Abs(trimmed.Seconds - original.Seconds) <= Math.Max(1, original.Seconds * 0.02);
    }
}
