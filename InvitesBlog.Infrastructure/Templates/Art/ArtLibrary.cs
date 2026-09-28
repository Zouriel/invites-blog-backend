using System.Text;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.TemplateCompiler.Design.Art;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Templates.Art;

/// <inheritdoc cref="IArtLibrary"/>
/// <remarks>
/// Every file — from a library or uploaded — takes the same road: an SVG is cut into layers by
/// <see cref="SvgArtConverter"/> (its animation turned into scroll keyframes), a GIF that moves becomes
/// a flipbook by <see cref="GifFlipbook"/>, anything else is one still picture; then every layer goes
/// through <see cref="IDesignEngine.ImportAsset"/>, the same sanitiser and size limits as any upload.
/// </remarks>
public sealed class ArtLibrary(IEnumerable<IArtSource> sources, IDesignEngine engine, ILogger<ArtLibrary> logger) : IArtLibrary
{
    /// <summary>What one piece of art may add to a page; a published page's hard limit is 800 KB for everything.</summary>
    public const int BudgetBytes = 360 * 1024;
    private const int MaxGifFrames = 12;

    private static readonly string[] Kinds = ["vector", "animated", "picture"];

    public IReadOnlyList<ArtSourceDto> Sources() => sources.Select(s => s.Describe()).ToList();

    private IArtSource Find(string? id) =>
        sources.FirstOrDefault(s => s.Id == id) ?? throw new NotFoundException("That library isn't one we search.", "art_source");

    public async Task<ArtSearchResultDto> SearchAsync(string source, string? query, string? kind, int page, CancellationToken ct = default)
    {
        var s = Find(source);
        var info = s.Describe();
        if (!info.Available) throw new BusinessRuleException($"{info.Name} isn't connected yet.", "art_unavailable");
        var k = Kinds.Contains(kind) ? kind! : "vector";
        var q = (query ?? string.Empty).Trim();
        if (q.Length > 80) q = q[..80];
        page = Math.Clamp(page, 1, 50);
        if (!info.Kinds.Contains(k) || q.Length == 0 && s.Id == "openclipart") return new ArtSearchResultDto([], page, false, 0);
        return await Limited(info.Name, SearchTimeout, ct, token => s.SearchAsync(q, k, page, token));
    }

    public async Task<ArtImportDto> ImportAsync(ArtImportRequest request, CancellationToken ct = default)
    {
        var s = Find(request.Source);
        if (!s.Describe().Available) throw new BusinessRuleException($"{s.Describe().Name} isn't connected yet.", "art_unavailable");
        if (string.IsNullOrWhiteSpace(request.Id) || request.Id.Length > 64) throw new NotFoundException("That picture isn't in the library.", "art_not_found");
        var file = await Limited(s.Describe().Name, FetchTimeout, ct, token => s.FetchAsync(request.Id.Trim(), request.Title, token));
        return Build(file.Content, file.ContentType, file.Title, file.Credit);
    }

    /// <summary>
    /// Libraries are free services and sometimes crawl (Openclipart has taken a minute to send one SVG),
    /// so each call gets a limit, and running out of it reads as the library being slow, not our error.
    /// </summary>
    private static readonly TimeSpan SearchTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(90);

    private static async Task<T> Limited<T>(string name, TimeSpan limit, CancellationToken ct, Func<CancellationToken, Task<T>> call)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(limit);
        try
        {
            return await call(timeout.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or BusinessRuleException { ErrorCode: "art_unavailable" }
                                  && timeout.IsCancellationRequested && !ct.IsCancellationRequested)
        {
            throw new BusinessRuleException($"{name} is very slow right now — try again later, or search another library.", "art_slow");
        }
    }

    public ArtImportDto ImportFile(byte[] content, string fileName, string contentType)
    {
        if (content.Length == 0) throw new BusinessRuleException("That file is empty.", "asset_empty");
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.Length > 60) name = name[..60];
        var type = fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "image/svg+xml" : contentType;
        return Build(content, type, name.Length > 0 ? name : "Picture", null);
    }

    private ArtImportDto Build(byte[] content, string contentType, string title, ArtCreditDto? credit)
    {
        if (LooksSvg(content, contentType)) return BuildSvg(content, title, credit);
        if (ImageSniffer.Detect(content) == "image/gif" && GifFlipbook.Build(content, MaxGifFrames, BudgetBytes) is { } gif)
            return BuildGif(gif, title, credit);
        var still = engine.ImportAsset(content, title + Extension(content), ImageSniffer.Detect(content) ?? contentType);
        return new ArtImportDto(title, still.Width, still.Height, [still], [new ArtLayerDto(still.Id, null, [])], false, 0, 1, credit, still.Bytes);
    }

    private static string Extension(byte[] content) => ImageSniffer.Detect(content) switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/webp" => ".webp",
        "image/gif" => ".gif",
        _ => "",
    };

    private static bool LooksSvg(byte[] content, string contentType)
    {
        if (contentType.Contains("svg", StringComparison.OrdinalIgnoreCase)) return true;
        var head = Encoding.UTF8.GetString(content, 0, Math.Min(content.Length, 1024)).TrimStart('﻿', ' ', '\t', '\r', '\n');
        return head.StartsWith('<') && head.Contains("<svg", StringComparison.OrdinalIgnoreCase);
    }

    private ArtImportDto BuildSvg(byte[] content, string title, ArtCreditDto? credit)
    {
        var markup = Encoding.UTF8.GetString(content).TrimStart('﻿');
        ArtImportDto? best = null;
        // Flipbook stills are copies of the art: fewer of them when they don't fit.
        foreach (var frames in new[] { 8, 5, 3, 2 })
        {
            ArtPlan plan;
            try { plan = SvgArtConverter.Convert(markup, frames); }
            catch (ArtRejectedException e) { throw new BusinessRuleException(e.Message, "svg_invalid"); }

            var assets = new List<DesignAssetDto>();
            var layers = new List<ArtLayerDto>();
            foreach (var layer in plan.Layers)
            {
                var asset = engine.ImportAsset(Encoding.UTF8.GetBytes(layer.Svg), (layer.Name is { } n ? $"{title} · {n}" : title) + ".svg", "image/svg+xml");
                assets.Add(asset);
                layers.Add(new ArtLayerDto(asset.Id, layer.Name,
                    layer.Frames.Select(f => new ArtFrameDto(f.T, f.Dx, f.Dy, f.Rotate, f.Scale, f.Opacity)).ToList()));
            }
            var bytes = assets.Sum(a => a.Bytes);
            best = new ArtImportDto(title, plan.Width, plan.Height, assets, layers, plan.Animated, plan.Seconds, plan.Loops, credit, bytes);
            var flipbook = plan.Layers.Count(l => l.Frames.Count > 0 && l.Frames.All(f => f.Dx == 0 && f.Dy == 0 && f.Rotate == 0 && f.Scale == 1));
            if (bytes <= BudgetBytes || flipbook == 0) break;
        }
        if (best!.Bytes > BudgetBytes)
        {
            logger.LogInformation("Art {Title} refused at {Bytes} bytes", title, best.Bytes);
            throw new BusinessRuleException(
                $"“{title}” comes to {best.Bytes / 1024}KB — too heavy for an invitation (keep art under {BudgetBytes / 1024}KB).", "art_too_large");
        }
        return best;
    }

    private static ArtImportDto BuildGif(GifFlipbook.Result gif, string title, ArtCreditDto? credit)
    {
        var loops = SvgArtConverter.LoopsFor(gif.Seconds);
        var steps = SvgArtConverter.FlipbookSteps(gif.Frames.Count, loops);
        var assets = new List<DesignAssetDto>();
        var layers = new List<ArtLayerDto>();
        for (var j = 0; j < gif.Frames.Count; j++)
        {
            var frame = gif.Frames[j];
            var id = "a" + Guid.NewGuid().ToString("n")[..10];
            assets.Add(new DesignAssetDto(id, "image", $"data:image/webp;base64,{Convert.ToBase64String(frame.Webp)}",
                frame.Width, frame.Height, null, $"{title} {j + 1}", frame.Webp.Length));
            layers.Add(new ArtLayerDto(id, $"Frame {j + 1}",
                steps[j].Select(f => new ArtFrameDto(f.T, 0, 0, 0, 1, f.Opacity)).ToList()));
        }
        var first = gif.Frames[0];
        return new ArtImportDto(title, first.Width, first.Height, assets, layers, true, gif.Seconds * loops, loops, credit, assets.Sum(a => a.Bytes));
    }
}
