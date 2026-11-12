using System.Reflection;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Demo;

/// <summary>One sample photo of the demo event, as listed in <c>Demo/Photos/photos.json</c>.</summary>
public sealed record DemoPhoto(string File, int Width, int Height, int ThumbWidth, int ThumbHeight, long Bytes, string Description);

/// <summary>
/// The sample photos behind GET /api/demo/event (docs/GUIDANCE.md section 7). They ship in the repo
/// (<c>Demo/Photos</c>, embedded; sources and licences in its README) and are published to storage
/// under <see cref="Prefix"/> on every start-up, like templates are, so a fresh store heals itself.
/// </summary>
public static class DemoAssets
{
    /// <summary>
    /// Where they live in storage. Versioned: change the photos and bump it, so no cache anywhere keeps
    /// serving an old picture under a name it already knows.
    /// </summary>
    public const string Prefix = "demo/v1/";

    private const string ResourceMarker = ".Demo.Photos.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Lazy<IReadOnlyList<DemoPhoto>> Manifest = new(LoadManifest);

    /// <summary>The photos, in album order. The first two are the event's covers.</summary>
    public static IReadOnlyList<DemoPhoto> Photos => Manifest.Value;

    public static string Key(DemoPhoto photo) => $"{Prefix}{photo.File}.webp";
    public static string ThumbKey(DemoPhoto photo) => $"{Prefix}{photo.File}-thumb.webp";

    private static IReadOnlyList<DemoPhoto> LoadManifest()
    {
        var asm = typeof(DemoAssets).Assembly;
        var name = asm.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith(ResourceMarker + "photos.json", StringComparison.Ordinal));
        if (name is null) return [];
        using var stream = asm.GetManifestResourceStream(name)!;
        return JsonSerializer.Deserialize<List<DemoPhoto>>(stream, Json) ?? [];
    }

    /// <summary>The bytes of an embedded photo file ("01-rings.webp"), or null.</summary>
    internal static byte[]? Read(string fileName)
    {
        var asm = typeof(DemoAssets).Assembly;
        var name = asm.GetManifestResourceNames().SingleOrDefault(n => n.EndsWith(ResourceMarker + fileName, StringComparison.Ordinal));
        if (name is null) return null;
        using var stream = asm.GetManifestResourceStream(name)!;
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

/// <summary>Publishes the demo photos to storage at start-up. Never stops the API from starting.</summary>
public sealed class DemoAssetSeeder(IStorageService storage, ILogger<DemoAssetSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        var published = 0;
        foreach (var photo in DemoAssets.Photos)
        {
            foreach (var (file, key) in new[] { ($"{photo.File}.webp", DemoAssets.Key(photo)), ($"{photo.File}-thumb.webp", DemoAssets.ThumbKey(photo)) })
            {
                var bytes = DemoAssets.Read(file);
                if (bytes is null)
                {
                    logger.LogWarning("Demo photo {File} is listed but not embedded — skipped.", file);
                    continue;
                }
                try
                {
                    await storage.PutAsync(key, bytes, "image/webp", ct);
                    published++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Couldn't publish demo photo {Key}.", key);
                }
            }
        }
        logger.LogInformation("Published {Count} demo event files to storage.", published);
    }
}
