using System.Runtime.InteropServices;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace InvitesBlog.Infrastructure.Faces;

/// <summary>
/// Finds faces and fingerprints them on our own server, with two small open models from OpenCV's model zoo,
/// both free for commercial use: <b>YuNet</b> (face detection, MIT, 2023mar, 640×640 input) and <b>SFace</b>
/// (face recognition, Apache-2.0, 2021dec, 128 numbers per face). The InsightFace models are better known but
/// their weights are for non-commercial research only.
///
/// <para><b>How.</b> The picture is turned upright, scaled so its long edge is 640 and padded to 640×640
/// (top-left, as OpenCV's FaceDetectorYN pads), and YuNet's three output scales are decoded exactly as
/// FaceDetectorYN does, then overlapping boxes are thinned out (NMS). Each face is aligned from the
/// full-size picture to the standard 112×112 face (five landmarks, least-squares similarity transform,
/// bilinear sampling, as FaceRecognizerSF.alignCrop does) and SFace gives its fingerprint, made unit length so
/// that comparing two faces is a dot product (the same person scores ~0.75-0.9, two people below ~0.4).</para>
///
/// <para><b>Where it runs.</b> On the GPU when there is one (CUDA 12, which still supports the server's
/// Pascal card; CUDA 13 doesn't), else on the CPU (<c>Faces:Device</c>: auto, gpu or cpu). Both models
/// together are ~40 MB, so this is fast either way.</para>
///
/// <para><b>Memory.</b> Like <see cref="Images.BackgroundRemover"/>: the models are loaded for work and released
/// <c>Faces:IdleSeconds</c> (default 60) after the last picture, never while one is being read, with the CPU
/// arena off and freed memory handed back to the system. On the GPU the CUDA EP's own arena goes with the
/// sessions; the CUDA context the process opened stays (a driver matter, measured on the server).</para>
/// </summary>
public sealed class FaceAnalyzer(IConfiguration config, ILogger<FaceAnalyzer> log) : IFaceAnalyzer, IDisposable
{
    public const int DetectSize = 640;
    public const int FaceSize = 112;
    private static readonly int[] Strides = [8, 16, 32];

    /// <summary>Where the five landmarks go in the 112×112 face (eyes, nose tip, mouth corners), as SFace was trained.</summary>
    private static readonly float[,] Reference =
    {
        { 38.2946f, 51.6963f }, { 73.5318f, 51.5014f }, { 56.0252f, 71.7366f }, { 41.5493f, 92.3655f }, { 70.7299f, 92.2041f },
    };

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object loading = new();
    private InferenceSession? detector;
    private InferenceSession? recognizer;
    private Timer? releaseTimer;

    /// <summary>The device the loaded models run on ("gpu" or "cpu"), null when unloaded.</summary>
    public string? Device { get; private set; }

    public bool Loaded => detector is not null;

    private string ModelDir => config["Faces:ModelDir"] is { Length: > 0 } d ? d : Path.Combine(AppContext.BaseDirectory, "models");
    private string DetectorPath => config["Faces:DetectorPath"] is { Length: > 0 } p ? p : Path.Combine(ModelDir, "face_detection_yunet_2023mar.onnx");
    private string RecognizerPath => config["Faces:RecognizerPath"] is { Length: > 0 } p ? p : Path.Combine(ModelDir, "face_recognition_sface_2021dec.onnx");
    private TimeSpan IdleRelease => TimeSpan.FromSeconds(int.TryParse(config["Faces:IdleSeconds"], out var s) && s >= 0 ? s : 60);
    private float MinScore => float.TryParse(config["Faces:MinScore"], System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0.8f;

    /// <summary>Faces narrower than this (in the picture's own pixels) are too small to fingerprint reliably and are left out.</summary>
    private int MinFacePixels => int.TryParse(config["Faces:MinFacePixels"], out var v) ? v : 36;

    public bool Available => File.Exists(DetectorPath) && File.Exists(RecognizerPath);

    public async Task<IReadOnlyList<DetectedFace>> AnalyzeAsync(byte[] content, CancellationToken ct)
    {
        if (!Available) throw new InvalidStateException("Face grouping isn't available right now.", "faces_unavailable");
        Image<Rgb24> image;
        try
        {
            image = Image.Load<Rgb24>(content);
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            throw new BusinessRuleException("That picture couldn't be read.", "photo_invalid");
        }

        using (image)
        {
            image.Mutate(x => x.AutoOrient());
            await gate.WaitAsync(ct);
            try
            {
                releaseTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                return await Task.Run(() => Analyze(image), ct);
            }
            finally
            {
                releaseTimer ??= new Timer(_ => Release(), null, Timeout.Infinite, Timeout.Infinite);
                releaseTimer.Change(IdleRelease, Timeout.InfiniteTimeSpan);
                gate.Release();
            }
        }
    }

    private List<DetectedFace> Analyze(Image<Rgb24> image)
    {
        var (det, rec) = Sessions();
        var pixels = new byte[image.Width * image.Height * 3];
        image.CopyPixelDataTo(pixels);

        var found = Detect(det, image);
        var faces = new List<DetectedFace>();
        foreach (var f in found)
        {
            var w = f.W;
            if (w < MinFacePixels) continue;
            var landmarks = new float[5, 2];
            for (var i = 0; i < 5; i++)
            {
                landmarks[i, 0] = f.Landmarks[i * 2];
                landmarks[i, 1] = f.Landmarks[i * 2 + 1];
            }
            var embedding = Fingerprint(rec, pixels, image.Width, image.Height, landmarks);
            float x = f.X, y = f.Y, h = f.H;
            x = Math.Max(0, x);
            y = Math.Max(0, y);
            faces.Add(new DetectedFace(
                x / image.Width, y / image.Height,
                Math.Min(w, image.Width - x) / image.Width, Math.Min(h, image.Height - y) / image.Height,
                f.Score, embedding));
        }
        return faces;
    }

    private (InferenceSession Detector, InferenceSession Recognizer) Sessions()
    {
        if (detector is not null && recognizer is not null) return (detector, recognizer);
        lock (loading)
        {
            if (detector is not null && recognizer is not null) return (detector, recognizer);
            var want = (config["Faces:Device"] ?? "auto").Trim().ToLowerInvariant();
            if (want is "auto" or "gpu")
            {
                try
                {
                    detector = new InferenceSession(DetectorPath, GpuOptions());
                    recognizer = new InferenceSession(RecognizerPath, GpuOptions());
                    Device = "gpu";
                }
                catch (Exception e) when (e is OnnxRuntimeException or DllNotFoundException or EntryPointNotFoundException)
                {
                    detector?.Dispose();
                    detector = null;
                    if (want == "gpu") throw;
                    log.LogWarning("Face models can't use the GPU ({Reason}); using the CPU", e.Message);
                }
            }
            if (detector is null)
            {
                detector = new InferenceSession(DetectorPath, CpuOptions());
                recognizer = new InferenceSession(RecognizerPath, CpuOptions());
                Device = "cpu";
            }
            log.LogInformation("Face models loaded on the {Device}", Device);
            return (detector, recognizer!);
        }
    }

    private static SessionOptions CpuOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)),
        EnableCpuMemArena = false,
        EnableMemoryPattern = false,
    };

    private static SessionOptions GpuOptions()
    {
        // The runtime's environment (and its logger) must exist before CUDA options are made, or creating them
        // fails with "Attempt to use DefaultLogger but none has been registered" and faces fall back to the CPU.
        _ = OrtEnv.Instance();
        using var cuda = new OrtCUDAProviderOptions();
        cuda.UpdateOptions(new Dictionary<string, string>
        {
            ["device_id"] = "0",
            // Grow only by what a run asks for, so the arena stays the size of the work, not double it.
            ["arena_extend_strategy"] = "kSameAsRequested",
            ["cudnn_conv_algo_search"] = "HEURISTIC",
        });
        var options = SessionOptions.MakeSessionOptionWithCudaProvider(cuda);
        options.GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL;
        options.EnableCpuMemArena = false;
        options.EnableMemoryPattern = false;
        return options;
    }

    private sealed record Box(float X, float Y, float W, float H, float Score, float[] Landmarks);

    /// <summary>
    /// How large the picture is scanned in tiles as well, for small faces in crowd shots (<c>Faces:TileEdge</c>;
    /// 0 = off). By default 1280 on the GPU, where the four extra passes cost little, and off on the CPU.
    /// </summary>
    private int TileEdge => int.TryParse(config["Faces:TileEdge"], out var v) ? v : Device == "gpu" ? 1280 : 0;

    /// <summary>
    /// The faces in the picture, in its own pixels: the whole picture at 640, and, when <see cref="TileEdge"/>
    /// is on and the picture is big enough, overlapping 640×640 tiles of it scaled to that size. A face seen
    /// twice is kept once (the surer box).
    /// </summary>
    private List<Box> Detect(InferenceSession det, Image<Rgb24> image)
    {
        var longest = Math.Max(image.Width, image.Height);
        var boxes = DetectAt(det, image, (float)DetectSize / longest, whole: true);
        var tile = TileEdge;
        if (tile > DetectSize && longest > DetectSize * 1.25f)
            boxes.AddRange(DetectAt(det, image, (float)Math.Min(tile, longest) / longest, whole: false));
        return Suppress(boxes, 0.3f);
    }

    /// <summary>Overlap between tiles, so a face on a seam is whole in one of them.</summary>
    private const int TileOverlap = 160;

    private List<Box> DetectAt(InferenceSession det, Image<Rgb24> image, float scale, bool whole)
    {
        var w = Math.Max(1, (int)MathF.Round(image.Width * scale));
        var h = Math.Max(1, (int)MathF.Round(image.Height * scale));
        using var scaled = image.Clone(x => x.Resize(w, h, scale < 1 ? KnownResamplers.Box : KnownResamplers.Triangle));
        var boxes = new List<Box>();
        foreach (var oy in whole ? [0] : Starts(h))
        foreach (var ox in whole ? [0] : Starts(w))
        {
            foreach (var b in DetectWindow(det, scaled, ox, oy))
            {
                // A face cut by a tile's inner edge is found whole in the next tile (they overlap): skip the half.
                if (!whole && ((ox > 0 && b.X < 4) || (oy > 0 && b.Y < 4)
                               || (ox + DetectSize < w && b.X + b.W > DetectSize - 4)
                               || (oy + DetectSize < h && b.Y + b.H > DetectSize - 4)))
                    continue;
                var lm = new float[10];
                for (var i = 0; i < 5; i++)
                {
                    lm[i * 2] = (b.Landmarks[i * 2] + ox) / scale;
                    lm[i * 2 + 1] = (b.Landmarks[i * 2 + 1] + oy) / scale;
                }
                boxes.Add(new Box((b.X + ox) / scale, (b.Y + oy) / scale, b.W / scale, b.H / scale, b.Score, lm));
            }
        }
        return boxes;
    }

    /// <summary>Where tiles start along one side so they cover it with <see cref="TileOverlap"/>.</summary>
    private static int[] Starts(int length)
    {
        if (length <= DetectSize) return [0];
        var starts = new List<int>();
        for (var s = 0; ; s += DetectSize - TileOverlap)
        {
            if (s + DetectSize >= length)
            {
                starts.Add(length - DetectSize);
                break;
            }
            starts.Add(s);
        }
        return starts.Distinct().ToArray();
    }

    /// <summary>YuNet on one 640×640 window of the picture (padded black past its edge); boxes in that window's pixels.</summary>
    private List<Box> DetectWindow(InferenceSession det, Image<Rgb24> scaled, int ox, int oy)
    {
        // BGR, 0-255, unnormalised: what FaceDetectorYN feeds it. The padding stays black.
        // One flat planar buffer written row by row: DenseTensor's 4-index setter costs more than the model.
        const int plane = DetectSize * DetectSize;
        var buffer = new float[3 * plane];
        scaled.ProcessPixelRows(rows =>
        {
            var h = Math.Min(DetectSize, rows.Height - oy);
            for (var y = 0; y < h; y++)
            {
                var row = rows.GetRowSpan(oy + y);
                var w = Math.Min(DetectSize, row.Length - ox);
                var bPlane = buffer.AsSpan(y * DetectSize, w);
                var gPlane = buffer.AsSpan(plane + y * DetectSize, w);
                var rPlane = buffer.AsSpan(2 * plane + y * DetectSize, w);
                var src = row.Slice(ox, w);
                for (var x = 0; x < w; x++)
                {
                    var px = src[x];
                    bPlane[x] = px.B;
                    gPlane[x] = px.G;
                    rPlane[x] = px.R;
                }
            }
        });
        var input = new DenseTensor<float>(buffer, [1, 3, DetectSize, DetectSize]);

        using var results = det.Run([NamedOnnxValue.CreateFromTensor(det.InputMetadata.Keys.First(), input)]);
        var outputs = results.ToDictionary(r => r.Name, r => r.AsTensor<float>().ToArray());
        var boxes = new List<Box>();
        var minScore = MinScore;
        foreach (var stride in Strides)
        {
            var cols = DetectSize / stride;
            var rows = DetectSize / stride;
            var cls = outputs[$"cls_{stride}"];
            var obj = outputs[$"obj_{stride}"];
            var bbox = outputs[$"bbox_{stride}"];
            var kps = outputs[$"kps_{stride}"];
            for (var r = 0; r < rows; r++)
            for (var c = 0; c < cols; c++)
            {
                var i = r * cols + c;
                var score = MathF.Sqrt(Math.Clamp(cls[i], 0f, 1f) * Math.Clamp(obj[i], 0f, 1f));
                if (score < minScore) continue;
                var cx = (c + bbox[i * 4]) * stride;
                var cy = (r + bbox[i * 4 + 1]) * stride;
                var bw = MathF.Exp(bbox[i * 4 + 2]) * stride;
                var bh = MathF.Exp(bbox[i * 4 + 3]) * stride;
                var landmarks = new float[10];
                for (var n = 0; n < 5; n++)
                {
                    landmarks[n * 2] = (kps[i * 10 + n * 2] + c) * stride;
                    landmarks[n * 2 + 1] = (kps[i * 10 + n * 2 + 1] + r) * stride;
                }
                boxes.Add(new Box(cx - bw / 2, cy - bh / 2, bw, bh, score, landmarks));
            }
        }
        return boxes;
    }

    /// <summary>Keeps the surest of boxes that overlap by more than <paramref name="iou"/>.</summary>
    private static List<Box> Suppress(List<Box> boxes, float iou)
    {
        var kept = new List<Box>();
        foreach (var b in boxes.OrderByDescending(b => b.Score))
        {
            if (kept.All(k => Overlap(k, b) <= iou)) kept.Add(b);
        }
        return kept;
    }

    private static float Overlap(Box a, Box b)
    {
        var x1 = Math.Max(a.X, b.X);
        var y1 = Math.Max(a.Y, b.Y);
        var x2 = Math.Min(a.X + a.W, b.X + b.W);
        var y2 = Math.Min(a.Y + a.H, b.Y + b.H);
        var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        var union = a.W * a.H + b.W * b.H - inter;
        return union <= 0 ? 0 : inter / union;
    }

    /// <summary>
    /// The face aligned to the 112×112 reference and fingerprinted. The transform is the least-squares
    /// similarity (rotation, one scale, shift) taking the landmarks onto <see cref="Reference"/>.
    /// </summary>
    private static float[] Fingerprint(InferenceSession rec, byte[] rgb, int width, int height, float[,] landmarks)
    {
        var (a, b, tx, ty) = Similarity(landmarks);
        // Inverse: source = R⁻¹ (dest - t), where R = [a -b; b a].
        var n = a * a + b * b;
        float ia = a / n, ib = b / n;
        const int plane = FaceSize * FaceSize;
        var buffer = new float[3 * plane];
        for (var v = 0; v < FaceSize; v++)
        for (var u = 0; u < FaceSize; u++)
        {
            float dx = u - tx, dy = v - ty;
            var sx = ia * dx + ib * dy;
            var sy = -ib * dx + ia * dy;
            Sample(rgb, width, height, sx, sy, out var r, out var g, out var bl);
            // RGB, 0-255: FaceRecognizerSF swaps OpenCV's BGR to RGB before the model.
            var i = v * FaceSize + u;
            buffer[i] = r;
            buffer[plane + i] = g;
            buffer[2 * plane + i] = bl;
        }
        var input = new DenseTensor<float>(buffer, [1, 3, FaceSize, FaceSize]);

        using var results = rec.Run([NamedOnnxValue.CreateFromTensor(rec.InputMetadata.Keys.First(), input)]);
        var e = results.First().AsTensor<float>().ToArray();
        var norm = MathF.Sqrt(e.Sum(x => x * x));
        if (norm > 0) for (var i = 0; i < e.Length; i++) e[i] /= norm;
        return e;
    }

    /// <summary>Least-squares similarity transform from the landmarks to <see cref="Reference"/>: dest = [a -b; b a]·src + t.</summary>
    public static (float A, float B, float Tx, float Ty) Similarity(float[,] src)
    {
        float msx = 0, msy = 0, mdx = 0, mdy = 0;
        for (var i = 0; i < 5; i++)
        {
            msx += src[i, 0]; msy += src[i, 1];
            mdx += Reference[i, 0]; mdy += Reference[i, 1];
        }
        msx /= 5; msy /= 5; mdx /= 5; mdy /= 5;
        float num1 = 0, num2 = 0, den = 0;
        for (var i = 0; i < 5; i++)
        {
            float sx = src[i, 0] - msx, sy = src[i, 1] - msy;
            float dx = Reference[i, 0] - mdx, dy = Reference[i, 1] - mdy;
            num1 += sx * dx + sy * dy;
            num2 += sx * dy - sy * dx;
            den += sx * sx + sy * sy;
        }
        if (den <= 0) den = 1;
        var a = num1 / den;
        var b = num2 / den;
        return (a, b, mdx - (a * msx - b * msy), mdy - (b * msx + a * msy));
    }

    /// <summary>Bilinear sample; black outside the picture (as warpAffine's constant border).</summary>
    private static void Sample(byte[] rgb, int width, int height, float x, float y, out float r, out float g, out float b)
    {
        var x0 = (int)MathF.Floor(x);
        var y0 = (int)MathF.Floor(y);
        float fx = x - x0, fy = y - y0;
        r = g = b = 0;
        for (var j = 0; j < 2; j++)
        for (var i = 0; i < 2; i++)
        {
            int px = x0 + i, py = y0 + j;
            if (px < 0 || py < 0 || px >= width || py >= height) continue;
            var wgt = (i == 0 ? 1 - fx : fx) * (j == 0 ? 1 - fy : fy);
            var o = (py * width + px) * 3;
            r += rgb[o] * wgt;
            g += rgb[o + 1] * wgt;
            b += rgb[o + 2] * wgt;
        }
    }

    /// <summary>Unloads the models (unless a picture is being read) and returns the freed memory to the system.</summary>
    public void Release()
    {
        if (!gate.Wait(0)) return;
        try
        {
            lock (loading)
            {
                if (detector is null && recognizer is null) return;
                detector?.Dispose();
                recognizer?.Dispose();
                detector = null;
                recognizer = null;
                Device = null;
            }
            Configuration.Default.MemoryAllocator.ReleaseRetainedResources();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            TrimNativeHeap();
            log.LogInformation("Face models released");
        }
        finally
        {
            gate.Release();
        }
    }

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(UIntPtr pad);

    private static void TrimNativeHeap()
    {
        if (!OperatingSystem.IsLinux()) return;
        try { MallocTrim(UIntPtr.Zero); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    public void Dispose()
    {
        releaseTimer?.Dispose();
        detector?.Dispose();
        recognizer?.Dispose();
        gate.Dispose();
    }
}
