using System.Text.Json;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Faces;
using InvitesBlog.Infrastructure.Faces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>Skipped, not passed, without the face models (FACE_MODELS: a folder with both .onnx files) and samples.</summary>
public sealed class FaceModelFactAttribute : FactAttribute
{
    public FaceModelFactAttribute()
    {
        var dir = Environment.GetEnvironmentVariable("FACE_MODELS");
        var samples = Environment.GetEnvironmentVariable("FACE_SAMPLES");
        if (string.IsNullOrWhiteSpace(dir) || !File.Exists(Path.Combine(dir, "face_detection_yunet_2023mar.onnx"))
            || string.IsNullOrWhiteSpace(samples) || !File.Exists(Path.Combine(samples, "ref.json")))
            Skip = "Set FACE_MODELS (YuNet + SFace .onnx) and FACE_SAMPLES (photos + ref.json from OpenCV) to run with the real models.";
    }
}

/// <summary>
/// The face models, checked against OpenCV's own FaceDetectorYN + FaceRecognizerSF run on the same photos
/// (FACE_SAMPLES/ref.json, written by tools/faces/reference.py, which runs OpenCV: never typed by hand). The
/// samples are public-domain NASA and White House portraits: two photos each of two people, a family of four,
/// a cabinet of 23, and one more person.
/// </summary>
public class FaceAnalyzerTests
{
    private static readonly string? Models = Environment.GetEnvironmentVariable("FACE_MODELS");
    private static readonly string? Samples = Environment.GetEnvironmentVariable("FACE_SAMPLES");

    private static FaceAnalyzer Analyzer(string? dir, string device = "cpu", int idle = 60) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Faces:ModelDir"] = dir,
            ["Faces:Device"] = device,
            ["Faces:IdleSeconds"] = idle.ToString(),
        }).Build(),
        NullLogger<FaceAnalyzer>.Instance);

    private sealed record RefFace(float[] box, float score, float[] emb);

    [Fact]
    public async Task Without_the_models_it_says_unavailable()
    {
        var analyzer = Analyzer("/nowhere");
        Assert.False(analyzer.Available);
        await Assert.ThrowsAsync<InvalidStateException>(() => analyzer.AnalyzeAsync([1, 2, 3], CancellationToken.None));
    }

    [Fact]
    public void The_alignment_maps_the_reference_landmarks_onto_themselves()
    {
        var reference = new float[,] { { 38.2946f, 51.6963f }, { 73.5318f, 51.5014f }, { 56.0252f, 71.7366f }, { 41.5493f, 92.3655f }, { 70.7299f, 92.2041f } };
        var (a, b, tx, ty) = FaceAnalyzer.Similarity(reference);
        Assert.Equal(1f, a, 3);
        Assert.Equal(0f, b, 3);
        Assert.Equal(0f, tx, 2);
        Assert.Equal(0f, ty, 2);

        // Twice the size, turned 90°, moved: the transform undoes exactly that.
        var moved = new float[5, 2];
        for (var i = 0; i < 5; i++)
        {
            moved[i, 0] = -2 * reference[i, 1] + 300;
            moved[i, 1] = 2 * reference[i, 0] + 100;
        }
        var (a2, b2, _, _) = FaceAnalyzer.Similarity(moved);
        Assert.Equal(0f, a2, 3);
        Assert.Equal(-0.5f, b2, 3);
    }

    [FaceModelFact]
    public async Task Finds_the_same_faces_as_OpenCV_and_tells_people_apart()
    {
        var reference = JsonSerializer.Deserialize<Dictionary<string, List<RefFace>>>(
            await File.ReadAllTextAsync(Path.Combine(Samples!, "ref.json")))!;
        var analyzer = Analyzer(Models);
        var mine = new Dictionary<string, IReadOnlyList<Application.Abstractions.DetectedFace>>();
        foreach (var (name, expected) in reference)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(Samples!, name + ".jpg"));
            var size = SixLabors.ImageSharp.Image.Identify(bytes);
            var found = await analyzer.AnalyzeAsync(bytes, CancellationToken.None);
            mine[name] = found;

            // Every face OpenCV found big enough to keep, found here too, in the same place.
            var big = expected.Where(e => e.box[2] >= 36).ToList();
            Assert.True(found.Count == big.Count, $"{name}: {found.Count} faces, OpenCV {big.Count}");
            foreach (var e in big)
            {
                var ex = new[] { e.box[0] / size.Width, e.box[1] / size.Height, e.box[2] / size.Width, e.box[3] / size.Height };
                var best = found.Max(f => Iou(ex, f));
                Assert.True(best > 0.8, $"{name}: a face OpenCV found at {string.Join(",", e.box)} overlaps only {best:0.00}");
            }
        }

        float Sim(string a, string b) => FaceClustering.Similarity(mine[a][0].Embedding, mine[b][0].Embedding);
        Assert.True(Sim("collins1", "collins2") > 0.7, $"same person: {Sim("collins1", "collins2")}");
        Assert.True(Sim("obama1", "obama2") > 0.7, $"same person: {Sim("obama1", "obama2")}");
        Assert.True(Sim("collins1", "obama1") < FaceClustering.DefaultThreshold);
        Assert.True(Sim("kelly", "obama2") < FaceClustering.DefaultThreshold);
        // The family photo's father is the same person as the portraits; no one else in it is.
        var family = mine["family"];
        var fathers = family.Count(f => FaceClustering.Similarity(f.Embedding, mine["obama1"][0].Embedding) > FaceClustering.DefaultThreshold);
        Assert.Equal(1, fathers);
    }

    [FaceModelFact]
    public async Task Lets_the_models_go_after_the_idle_time()
    {
        var analyzer = Analyzer(Models, idle: 1);
        await analyzer.AnalyzeAsync(await File.ReadAllBytesAsync(Path.Combine(Samples!, "kelly.jpg")), CancellationToken.None);
        Assert.True(analyzer.Loaded);
        for (var i = 0; i < 50 && analyzer.Loaded; i++) await Task.Delay(100);
        Assert.False(analyzer.Loaded);
    }

    private static float Iou(float[] a, Application.Abstractions.DetectedFace b)
    {
        var x1 = Math.Max(a[0], b.X);
        var y1 = Math.Max(a[1], b.Y);
        var x2 = Math.Min(a[0] + a[2], b.X + b.W);
        var y2 = Math.Min(a[1] + a[3], b.Y + b.H);
        var inter = Math.Max(0, x2 - x1) * Math.Max(0, y2 - y1);
        return inter / (a[2] * a[3] + b.W * b.H - inter);
    }
}
