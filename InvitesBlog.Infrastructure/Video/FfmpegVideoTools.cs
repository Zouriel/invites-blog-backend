using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Video;

/// <inheritdoc cref="IVideoTools"/>
public sealed class FfmpegVideoTools : IVideoTools
{
    /// <summary>What stays free on the working disk, because other projects' containers live on it too.</summary>
    private const long DiskMargin = 2L * 1024 * 1024 * 1024;

    /// <summary>A stream copy or a probe takes seconds; if it takes this long something is wrong with the file.</summary>
    private static readonly TimeSpan QuickLimit = TimeSpan.FromMinutes(5);

    private readonly ILogger<FfmpegVideoTools> _logger;
    private readonly string _ffmpeg;
    private readonly string _ffprobe;
    private readonly string _root;
    private readonly string _preset;
    private readonly int _crf;
    private readonly int _threads;
    private readonly TimeSpan _limit;
    private readonly Lazy<bool> _available;

    public FfmpegVideoTools(IConfiguration config, ILogger<FfmpegVideoTools> logger)
    {
        _logger = logger;
        _ffmpeg = config["Video:Ffmpeg"] ?? "ffmpeg";
        _ffprobe = config["Video:Ffprobe"] ?? "ffprobe";
        _root = config["Video:WorkDir"] ?? Path.Combine(Path.GetTempPath(), "ib-video");
        _preset = config["Video:Preset"] ?? "medium";
        _crf = Math.Clamp(config.GetValue("Video:Crf", 23), 16, 32);
        _threads = Math.Clamp(config.GetValue("Video:Threads", 2), 1, 8);
        _limit = TimeSpan.FromMinutes(Math.Clamp(config.GetValue("Video:TimeLimitMinutes", 40.0), 0.01, 240));
        _available = new Lazy<bool>(() => Works(_ffmpeg) && Works(_ffprobe));
    }

    public bool Available => _available.Value;

    private bool Works(string tool)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(tool, "-version")
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            });
            if (p is null) return false;
            _ = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            return p.WaitForExit(15_000) && p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning("{Tool} isn't available, so videos stay on the old route and aren't trimmed.", tool);
            return false;
        }
    }

    public string NewWorkDir()
    {
        var dir = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    public bool HasRoomFor(long bytes)
    {
        Directory.CreateDirectory(_root);
        return new DriveInfo(Path.GetFullPath(_root)).AvailableFreeSpace >= bytes + DiskMargin;
    }

    public void ClearWorkDir()
    {
        try
        {
            // Only what's older than this process: the upload processor starts before this runs and may
            // already have a video in a folder of its own here.
            if (!Directory.Exists(_root)) return;
            var started = Process.GetCurrentProcess().StartTime.ToUniversalTime();
            foreach (var dir in new DirectoryInfo(_root).EnumerateDirectories())
                if (dir.CreationTimeUtc < started && dir.LastWriteTimeUtc < started) dir.Delete(recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Couldn't clear the video working folder.");
        }
    }

    /// <summary>Goes before -i on every run: only a local file, read as the family its bytes showed.</summary>
    private static IEnumerable<string> Input(string path, VideoKind kind) =>
        ["-protocol_whitelist", "file", "-f", kind.Demuxer, "-i", path];

    /// <summary>Only the main picture and sound, so a phone's location track or cover art can't ride along.</summary>
    private static readonly string[] OnlyPictureAndSound =
        ["-map", "0:V:0", "-map", "0:a:0?", "-map_metadata", "-1", "-map_chapters", "-1", "-sn", "-dn"];

    private static IEnumerable<string> Base => ["-nostdin", "-y", "-hide_banner", "-loglevel", "error"];

    public async Task<VideoProbe?> ProbeAsync(string path, VideoKind kind, CancellationToken ct = default)
    {
        var run = await RunAsync(_ffprobe,
            ["-v", "error", "-protocol_whitelist", "file", "-f", kind.Demuxer, "-show_streams", "-show_format", "-of", "json", path],
            QuickLimit, capture: true, ct);
        if (run.Outcome != VideoCompressOutcome.Done) return null;
        try
        {
            return Read(run.Output);
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            _logger.LogWarning("ffprobe's answer couldn't be read: {Type}", e.GetType().Name);
            return null;
        }
    }

    /// <summary>Public for its tests: the rotation and HDR rules are easy to get wrong.</summary>
    public static VideoProbe? Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("streams", out var streams)) return null;

        JsonElement? video = null;
        var hasAudio = false;
        foreach (var s in streams.EnumerateArray())
        {
            var type = Text(s, "codec_type");
            if (type == "audio") hasAudio = true;
            var attached = s.TryGetProperty("disposition", out var d) && d.TryGetProperty("attached_pic", out var a)
                && a.ValueKind == JsonValueKind.Number && a.GetInt32() == 1;
            if (type == "video" && !attached) video ??= s;
        }
        if (video is not { } v) return null;

        var width = Int(v, "width");
        var height = Int(v, "height");
        if (width <= 0 || height <= 0) return null;
        if (Math.Abs(Rotation(v)) % 180 == 90) (width, height) = (height, width);

        root.TryGetProperty("format", out var format);
        var seconds = format.ValueKind == JsonValueKind.Object ? Number(format, "duration") : 0;
        var bitRate = format.ValueKind == JsonValueKind.Object ? (long)Number(format, "bit_rate") : 0;
        var transfer = Text(v, "color_transfer");

        return new VideoProbe(
            Text(v, "codec_name") ?? string.Empty, width, height, seconds, bitRate, Rate(Text(v, "avg_frame_rate")),
            hasAudio, transfer is "smpte2084" or "arib-std-b67");
    }

    private static int Rotation(JsonElement stream)
    {
        if (stream.TryGetProperty("side_data_list", out var side) && side.ValueKind == JsonValueKind.Array)
            foreach (var entry in side.EnumerateArray())
                if (entry.TryGetProperty("rotation", out var r) && r.ValueKind == JsonValueKind.Number)
                    return (int)Math.Round(r.GetDouble());
        // Older ffmpeg reported it as a tag.
        return stream.TryGetProperty("tags", out var tags) && int.TryParse(Text(tags, "rotate"), out var rotate) ? rotate : 0;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;

    private static double Number(JsonElement e, string name) =>
        double.TryParse(Text(e, name), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : 0;

    private static double Rate(string? rate)
    {
        var parts = (rate ?? string.Empty).Split('/');
        return parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var a)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && b > 0
            ? a / b : 0;
    }

    public async Task<bool> StripMetadataAsync(string input, string output, VideoKind kind, CancellationToken ct = default)
    {
        string[] fast = kind.Muxer is "mp4" or "mov" ? ["-movflags", "+faststart"] : [];
        var run = await RunAsync(_ffmpeg,
            [.. Base, .. Input(input, kind), .. OnlyPictureAndSound, "-c", "copy", .. fast, "-f", kind.Muxer, output],
            QuickLimit, capture: false, ct);
        return run.Outcome == VideoCompressOutcome.Done && File.Exists(output) && new FileInfo(output).Length > 0;
    }

    public async Task<byte[]?> PosterAsync(string input, VideoKind kind, CancellationToken ct = default)
    {
        var dir = NewWorkDir();
        try
        {
            var output = Path.Combine(dir, "poster.jpg");
            // A moment in, because a video often opens on a black frame; from the start for a clip shorter than that.
            foreach (var at in new[] { "0.5", "0" })
            {
                var run = await RunAsync(_ffmpeg,
                    [.. Base, "-ss", at, .. Input(input, kind), "-map", "0:V:0", "-map_metadata", "-1", "-frames:v", "1",
                     "-vf", "scale='min(1280,iw)':-2", "-q:v", "3", "-f", "image2", output],
                    QuickLimit, capture: false, ct);
                if (run.Outcome == VideoCompressOutcome.Done && File.Exists(output) && new FileInfo(output).Length > 0)
                    return await File.ReadAllBytesAsync(output, ct);
            }
            return null;
        }
        finally
        {
            TryDelete(dir);
        }
    }

    public async Task<VideoCompressOutcome> CompressAsync(string input, string output, VideoKind kind, CancellationToken ct = default)
    {
        // ffmpeg turns the picture upright by itself before this runs, so these are the displayed sides.
        var edge = VideoTrimRules.MaxLongEdge.ToString(CultureInfo.InvariantCulture);
        var scale = $"scale=w='trunc(min(1,{edge}/max(iw,ih))*iw/2)*2':h='trunc(min(1,{edge}/max(iw,ih))*ih/2)*2'";
        var threads = _threads.ToString(CultureInfo.InvariantCulture);
        var run = await RunAsync(_ffmpeg,
            [.. Base, .. Input(input, kind), .. OnlyPictureAndSound, "-vf", scale,
             "-c:v", "libx264", "-preset", _preset, "-crf", _crf.ToString(CultureInfo.InvariantCulture),
             "-profile:v", "high", "-pix_fmt", "yuv420p", "-threads", threads, "-filter_threads", "1",
             "-c:a", "aac", "-b:a", "160k", "-movflags", "+faststart", "-f", "mp4", output],
            _limit, capture: false, ct, slow: true);
        return run.Outcome;
    }

    public async Task<bool> PlaysAsync(string path, VideoKind kind, double seconds, CancellationToken ct = default)
    {
        var start = await RunAsync(_ffmpeg,
            [.. Base, "-xerror", .. Input(path, kind), "-t", "3", "-f", "null", "-"], QuickLimit, capture: false, ct);
        if (start.Outcome != VideoCompressOutcome.Done) return false;
        if (seconds <= 6) return true;
        var at = (seconds - 3).ToString("0.###", CultureInfo.InvariantCulture);
        var end = await RunAsync(_ffmpeg,
            [.. Base, "-xerror", "-ss", at, .. Input(path, kind), "-f", "null", "-"], QuickLimit, capture: false, ct);
        return end.Outcome == VideoCompressOutcome.Done;
    }

    private readonly record struct RunResult(VideoCompressOutcome Outcome, string Output);

    /// <summary>
    /// One tool, one file. A nonzero exit, a time limit or a stop all kill the process, and only a stop
    /// (the app shutting down) throws: anything else is the file's doing and is for the caller to weigh.
    /// </summary>
    private async Task<RunResult> RunAsync(
        string tool, IEnumerable<string> args, TimeSpan limit, bool capture, CancellationToken ct, bool slow = false)
    {
        var info = new ProcessStartInfo
        {
            FileName = slow ? "nice" : tool,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = Path.GetTempPath(),
        };
        if (slow)
        {
            info.ArgumentList.Add("-n");
            info.ArgumentList.Add("15");
            info.ArgumentList.Add(tool);
        }
        foreach (var a in args) info.ArgumentList.Add(a);

        // The API's environment holds its secrets, and this process opens files strangers made.
        var path = info.Environment.TryGetValue("PATH", out var p) ? p : null;
        info.Environment.Clear();
        if (path is not null) info.Environment["PATH"] = path;

        using var process = new Process { StartInfo = info };
        try
        {
            process.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogError("{Tool} could not be started: {Error}", tool, e.Message);
            return new RunResult(VideoCompressOutcome.Failed, string.Empty);
        }
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = new CancellationTokenSource(limit);
        using var both = CancellationTokenSource.CreateLinkedTokenSource(ct, timeout.Token);
        try
        {
            await process.WaitForExitAsync(both.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            await process.WaitForExitAsync(CancellationToken.None);
            ct.ThrowIfCancellationRequested();
            _logger.LogWarning("{Tool} passed its time limit of {Limit} and was stopped.", tool, limit);
            return new RunResult(VideoCompressOutcome.TimedOut, string.Empty);
        }

        var text = await output;
        var said = await errors;
        if (process.ExitCode == 0) return new RunResult(VideoCompressOutcome.Done, capture ? text : string.Empty);

        // The end of what it said: paths here are ours, and nothing from the file's metadata is printed at this level.
        _logger.LogWarning("{Tool} exited {Code}: {Error}", tool, process.ExitCode, said.Length > 500 ? said[^500..] : said);
        return new RunResult(VideoCompressOutcome.Failed, string.Empty);
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Finished on its own in the meantime.
        }
    }

    private void TryDelete(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(e, "Couldn't remove a video working folder.");
        }
    }
}
