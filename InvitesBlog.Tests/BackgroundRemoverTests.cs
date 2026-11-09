using InvitesBlog.Application.Exceptions;
using InvitesBlog.Infrastructure.Images;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>Skipped, not passed, without the background model (BG_MODEL) to run.</summary>
public sealed class ModelFactAttribute : FactAttribute
{
    public ModelFactAttribute()
    {
        var model = Environment.GetEnvironmentVariable("BG_MODEL");
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model)) Skip = "Set BG_MODEL to isnet-general-use.onnx to run with the real model.";
    }
}

/// <summary>Writes cut-outs of real photos to look at: needs BG_MODEL, BG_SAMPLES and BG_OUT.</summary>
public sealed class SamplesFactAttribute : FactAttribute
{
    public SamplesFactAttribute()
    {
        var model = Environment.GetEnvironmentVariable("BG_MODEL");
        if (string.IsNullOrWhiteSpace(model) || !File.Exists(model) || Environment.GetEnvironmentVariable("BG_SAMPLES") is null || Environment.GetEnvironmentVariable("BG_OUT") is null)
            Skip = "Set BG_MODEL, BG_SAMPLES and BG_OUT to write cut-outs of real photos.";
    }
}

/// <summary>
/// "Remove background" with the real model. The model (~170 MB) isn't in the repo: these run when
/// BG_MODEL points at isnet-general-use.onnx (the API image ships it) and are skipped otherwise.
/// BG_SAMPLES (a folder of .jpg) and BG_OUT write cut-outs of real photos to look at.
/// </summary>
public class BackgroundRemoverTests
{
    private static readonly string? Model = Environment.GetEnvironmentVariable("BG_MODEL");

    private static BackgroundRemover Remover(string? path) => new(
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["BackgroundRemoval:ModelPath"] = path }).Build(),
        NullLogger<BackgroundRemover>.Instance);

    private static byte[] Png(Image<Rgba32> image)
    {
        using var ms = new MemoryStream();
        image.SaveAsPng(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task Without_the_model_it_says_unavailable()
    {
        var remover = Remover("/nowhere/isnet.onnx");
        Assert.False(remover.Available);
        await Assert.ThrowsAsync<InvalidStateException>(() => remover.CutOutAsync([1, 2, 3], CancellationToken.None));
    }

    [ModelFact]
    public async Task Cuts_a_subject_out_of_a_plain_background_and_keeps_its_size()
    {
        using var picture = new Image<Rgba32>(600, 400, new Rgba32(245, 245, 245));
        // A dark round thing in the middle, as a product shot or a portrait would have.
        for (var y = 0; y < 400; y++)
        for (var x = 0; x < 600; x++)
            if ((x - 300) * (x - 300) + (y - 200) * (y - 200) < 120 * 120) picture[x, y] = new Rgba32(40, 70, 140);
        var cut = await Remover(Model).CutOutAsync(Png(picture), CancellationToken.None);

        using var result = Image.Load<Rgba32>(cut);
        Assert.Equal(600, result.Width);
        Assert.Equal(400, result.Height);
        Assert.True(result[300, 200].A > 200, $"middle alpha {result[300, 200].A}");
        Assert.True(result[20, 20].A < 40, $"corner alpha {result[20, 20].A}");
        Assert.True(result[580, 380].A < 40, $"corner alpha {result[580, 380].A}");
        // Colours are kept where it's solid.
        Assert.InRange(result[300, 200].R, (byte)34, (byte)46);
    }

    [SamplesFact]
    public async Task Real_photos_come_out_for_a_look()
    {
        var samples = Environment.GetEnvironmentVariable("BG_SAMPLES");
        var output = Environment.GetEnvironmentVariable("BG_OUT");
        var remover = Remover(Model);
        foreach (var file in Directory.GetFiles(samples!, "*.jpg"))
        {
            var started = DateTime.UtcNow;
            var cut = await remover.CutOutAsync(await File.ReadAllBytesAsync(file), CancellationToken.None);
            await File.WriteAllBytesAsync(Path.Combine(output!, Path.GetFileNameWithoutExtension(file) + "-cut.png"), cut);
            Console.WriteLine($"{Path.GetFileName(file)}: {(DateTime.UtcNow - started).TotalMilliseconds:0} ms");
        }
    }
}
