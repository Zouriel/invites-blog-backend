using System.Runtime.InteropServices;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Png;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace InvitesBlog.Infrastructure.Images;

/// <summary>
/// Cuts the subject out of a picture on our own server: BiRefNet general "lite" (MIT, ZhengPeng7/BiRefNet,
/// as packaged by rembg), or ISNet general use (Apache-2.0), run with ONNX Runtime on the CPU. Nothing
/// leaves the server and nothing is paid per picture.
///
/// <para>The picture is scaled to the model's 1024×1024 input, its colours scaled to 0–1 and shifted by
/// ImageNet's means (as rembg does), and the model's mask, stretched between its own lowest and highest
/// values, is resized back to the picture and becomes its alpha. Pictures are capped at
/// <see cref="MaxEdge"/> first; one cut-out runs at a time (each takes about a second and ~1 GB).</para>
///
/// <para><b>Memory.</b> A cut-out needs a few GB while it runs, and ONNX Runtime keeps what it used by
/// default (its memory arena), which left the API at ~7 GB after a single picture. So the arena and the
/// memory-pattern cache are off, the model is loaded for a job and released <see cref="IdleRelease"/> after
/// the last one, and freed memory is handed back to the system (glibc keeps it otherwise).</para>
///
/// <para>Settings: <c>BackgroundRemoval:ModelPath</c> (default <c>models/isnet-general-use.onnx</c> next
/// to the app). Without the file the feature reports itself unavailable.</para>
/// </summary>
public sealed class BackgroundRemover(IConfiguration config, ILogger<BackgroundRemover> log) : IBackgroundRemover, IDisposable
{
    public const int Size = 1024;
    public const int MaxEdge = 2400;
    private static readonly float[] Mean = [0.485f, 0.456f, 0.406f];
    private static readonly float[] Spread = [0.229f, 0.224f, 0.225f];

    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly object loading = new();
    private InferenceSession? session;
    private Timer? releaseTimer;

    /// <summary>How long the model stays loaded after the last cut-out (<c>BackgroundRemoval:IdleSeconds</c>, default 60).</summary>
    private TimeSpan IdleRelease => TimeSpan.FromSeconds(int.TryParse(config["BackgroundRemoval:IdleSeconds"], out var s) && s >= 0 ? s : 60);

    /// <summary>Whether the model is in memory now (for tests and logs).</summary>
    public bool Loaded => session is not null;

    private string ModelPath => config["BackgroundRemoval:ModelPath"] is { Length: > 0 } p
        ? p
        : Path.Combine(AppContext.BaseDirectory, "models", "birefnet-general-lite.onnx");

    /// <summary>
    /// BiRefNet (as rembg runs it): ImageNet means and spreads in, a sigmoid on the way out. ISNet: means
    /// only, raw output. Taken from <c>BackgroundRemoval:Model</c>, else from the file's name.
    /// </summary>
    private bool BiRefNet => (config["BackgroundRemoval:Model"] ?? Path.GetFileName(ModelPath)).Contains("birefnet", StringComparison.OrdinalIgnoreCase);

    public bool Available => File.Exists(ModelPath);

    private InferenceSession Session()
    {
        if (session is not null) return session;
        lock (loading)
        {
            if (session is not null) return session;
            var options = new SessionOptions
            {
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
                IntraOpNumThreads = Math.Max(1, Math.Min(4, Environment.ProcessorCount / 2)),
                // Give memory back after each run instead of keeping a pool of the largest run's size.
                EnableCpuMemArena = false,
                EnableMemoryPattern = false,
            };
            session = new InferenceSession(ModelPath, options);
            log.LogInformation("Background removal model loaded from {Path}", ModelPath);
            return session;
        }
    }

    public async Task<byte[]> CutOutAsync(byte[] content, CancellationToken ct)
    {
        if (!Available) throw new InvalidStateException("Removing backgrounds isn't available right now.", "background_unavailable");
        Image<Rgba32> image;
        try
        {
            image = Image.Load<Rgba32>(content);
        }
        catch (Exception e) when (e is UnknownImageFormatException or InvalidImageContentException or NotSupportedException)
        {
            throw new BusinessRuleException("That picture couldn't be read.", "asset_invalid");
        }

        using (image)
        {
            image.Mutate(x => x.AutoOrient());
            if (Math.Max(image.Width, image.Height) > MaxEdge)
                image.Mutate(x => x.Resize(new ResizeOptions { Size = new Size(MaxEdge, MaxEdge), Mode = ResizeMode.Max }));

            await gate.WaitAsync(ct);
            float[] mask;
            try
            {
                releaseTimer?.Change(Timeout.Infinite, Timeout.Infinite);
                mask = await Task.Run(() => Predict(image), ct);
            }
            finally
            {
                // Keep it loaded a moment for the next picture; then let it go.
                releaseTimer ??= new Timer(_ => Release(), null, Timeout.Infinite, Timeout.Infinite);
                releaseTimer.Change(IdleRelease, Timeout.InfiniteTimeSpan);
                gate.Release();
            }

            using var alpha = MaskImage(mask);
            alpha.Mutate(x => x.Resize(image.Width, image.Height, KnownResamplers.Lanczos3));
            // Keep what was already see-through see-through.
            image.ProcessPixelRows(alpha, (pixels, mask2) =>
            {
                for (var y = 0; y < pixels.Height; y++)
                {
                    var row = pixels.GetRowSpan(y);
                    var m = mask2.GetRowSpan(y);
                    for (var x = 0; x < row.Length; x++) row[x].A = (byte)(row[x].A * m[x].PackedValue / 255);
                }
            });
            using var output = new MemoryStream();
            await image.SaveAsPngAsync(output, new PngEncoder { ColorType = PngColorType.RgbWithAlpha }, ct);
            return output.ToArray();
        }
    }

    /// <summary>The model's mask for the picture, 1024×1024, stretched to 0–1.</summary>
    private float[] Predict(Image<Rgba32> image)
    {
        using var input = image.Clone(x => x.Resize(Size, Size, KnownResamplers.Lanczos3));
        var tensor = new DenseTensor<float>([1, 3, Size, Size]);
        var spread = BiRefNet ? Spread : [1f, 1f, 1f];
        // As rembg: divide by the picture's own brightest value, subtract the mean, divide by the spread.
        var max = 1f;
        input.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
                foreach (var p in rows.GetRowSpan(y)) max = Math.Max(max, Math.Max(p.R, Math.Max(p.G, p.B)));
        });
        input.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < rows.Height; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < row.Length; x++)
                {
                    tensor[0, 0, y, x] = (row[x].R / max - Mean[0]) / spread[0];
                    tensor[0, 1, y, x] = (row[x].G / max - Mean[1]) / spread[1];
                    tensor[0, 2, y, x] = (row[x].B / max - Mean[2]) / spread[2];
                }
            }
        });
        var s = Session();
        var name = s.InputMetadata.Keys.First();
        using var results = s.Run([NamedOnnxValue.CreateFromTensor(name, tensor)]);
        var output = results.First().AsTensor<float>();
        var birefnet = BiRefNet;
        var values = new float[Size * Size];
        float lo = float.MaxValue, hi = float.MinValue;
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var v = output[0, 0, y, x];
            if (birefnet) v = 1f / (1f + MathF.Exp(-v));
            values[y * Size + x] = v;
            lo = Math.Min(lo, v);
            hi = Math.Max(hi, v);
        }
        var range = hi - lo;
        for (var i = 0; i < values.Length; i++) values[i] = range > 1e-6f ? (values[i] - lo) / range : 0;
        return values;
    }

    private static Image<L8> MaskImage(float[] mask)
    {
        var img = new Image<L8>(Size, Size);
        img.ProcessPixelRows(rows =>
        {
            for (var y = 0; y < Size; y++)
            {
                var row = rows.GetRowSpan(y);
                for (var x = 0; x < Size; x++) row[x] = new L8((byte)Math.Clamp(MathF.Round(mask[y * Size + x] * 255), 0, 255));
            }
        });
        return img;
    }

    /// <summary>Unloads the model (unless a cut-out is running) and returns the freed memory to the system.</summary>
    public void Release()
    {
        if (!gate.Wait(0)) return; // A job started meanwhile: its own timer releases it later.
        try
        {
            lock (loading)
            {
                if (session is null) return;
                session.Dispose();
                session = null;
            }
            // ImageSharp pools the big buffers a cut-out used; let those go too.
            Configuration.Default.MemoryAllocator.ReleaseRetainedResources();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            TrimNativeHeap();
            log.LogInformation("Background removal model released");
        }
        finally
        {
            gate.Release();
        }
    }

    [DllImport("libc", EntryPoint = "malloc_trim")]
    private static extern int MallocTrim(UIntPtr pad);

    /// <summary>glibc keeps freed memory for reuse; ask it to give it back (Linux only; elsewhere a no-op).</summary>
    private static void TrimNativeHeap()
    {
        if (!OperatingSystem.IsLinux()) return;
        try { MallocTrim(UIntPtr.Zero); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    public void Dispose()
    {
        releaseTimer?.Dispose();
        session?.Dispose();
        gate.Dispose();
    }
}
