using System.Text;
using System.Text.Json.Nodes;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.TemplateCompiler;
using InvitesBlog.TemplateCompiler.Design;
using InvitesBlog.TemplateCompiler.Design.Art;
using InvitesBlog.Infrastructure.Images;
using SixLabors.ImageSharp;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Infrastructure.Templates;

/// <summary>
/// <see cref="IDesignEngine"/> over the compiler. Named *Engine rather than *Service so the convention
/// scan leaves it alone; it's registered explicitly.
/// </summary>
public sealed class DesignEngine(
    RawTemplatePackager packager,
    IImageOptimizer optimizer,
    IConfiguration config) : IDesignEngine
{
    /// <summary>Largest scene the API accepts, before any compiling — pictures inside it included.</summary>
    public const int MaxSceneBytes = 3 * 1024 * 1024;

    private string FontBaseUrl => (config["Urls:AssetsBase"] ?? "/assets").TrimEnd('/') + "/fonts/";

    public object Catalog() => new
    {
        canvas = new { width = DesignCanvas.Width, referenceViewport = DesignCanvas.ReferenceViewport },
        fontBaseUrl = FontBaseUrl,
        fonts = DesignCatalog.Fonts,
        variables = DesignCatalog.Variables,
        enterPresets = DesignCatalog.EnterPresets,
        exitPresets = DesignCatalog.ExitPresets,
        loopPresets = DesignCatalog.LoopPresets,
        easings = DesignCatalog.Easings,
        fieldTypes = DesignCatalog.FieldTypes,
        linkPaths = DesignCatalog.LinkPaths,
        starters = DesignStarters.All,
        requiredThemeKeys = DesignCatalog.RequiredThemeKeys,
        limits = new
        {
            softBytes = DesignValidator.SoftBytes,
            hardBytes = DesignValidator.HardBytes,
            maxSvgBytes = DesignCatalog.MaxSvgBytes,
            maxImageBytes = DesignCatalog.MaxImageBytes,
            maxElements = DesignCatalog.MaxElements,
            maxKeyframes = DesignCatalog.MaxKeyframes,
            maxPageHeight = DesignCatalog.MaxPageHeight,
            maxDepth = DesignCatalog.MaxDepth,
            maxLoopRepeat = DesignCatalog.MaxLoopRepeat,
            maxBlur = DesignCatalog.MaxBlur,
            maxSkew = DesignCatalog.MaxSkew,
            maxSplitPieces = DesignCatalog.MaxSplitPieces,
            maxSceneBytes = MaxSceneBytes,
        },
    };

    public string Normalize(string sceneJson) => Parse(sceneJson).ToJson();

    public DesignBuild Build(string sceneJson, DesignBuildOptions options)
    {
        var scene = Parse(sceneJson);
        var issues = DesignValidator.Validate(scene).ToList();

        // What is published is the plain compile; the preview adds the editor bridge and data on top.
        var published = DesignCompiler.Compile(scene, new DesignCompileOptions { FontBaseUrl = FontBaseUrl, Title = options.Title });
        issues.AddRange(DesignValidator.CheckCompiled(published));

        var html = published;
        if (options.EditorPreview || options.Sample is not null)
        {
            html = DesignCompiler.Compile(scene, new DesignCompileOptions
            {
                FontBaseUrl = FontBaseUrl,
                Title = options.Title,
                EditorPreview = options.EditorPreview,
                InitialScroll = options.Scroll,
                HiddenElementIds = options.Hidden,
            });
            if (options.Sample is not null)
                html = ServerBinder.Bind(html, DesignSampleData.Build(scene, options.Sample, options.Blocks));
        }

        var manifest = packager.BuildManifest("design", "preview", published);
        var structure = new TemplateStructure(
            manifest.Fields.Select(f => $"{f.Label} ({f.Type})").ToList(),
            manifest.ImageSlots.Select(s => s.Multiple ? $"{s.Label} (gallery)" : s.Label).ToList(),
            manifest.Roles.ToList(),
            manifest.Theme.Keys.Select(k => k.Key).ToList());

        var dtoIssues = Collapse(issues);
        return new DesignBuild(
            html,
            Encoding.UTF8.GetByteCount(published),
            dtoIssues,
            structure,
            dtoIssues.Any(i => i.Severity == DesignIssue.Error));
    }

    public string? Starter(string id) => DesignStarters.Create(id)?.ToJson();

    public bool IsDesignedScene(string? sceneJson)
    {
        if (string.IsNullOrWhiteSpace(sceneJson) || !sceneJson.Contains("schema", StringComparison.Ordinal)) return false;
        try { return DesignScene.Parse(sceneJson).Schema == DesignScene.CurrentSchema; }
        catch (DesignSceneException) { return false; }
    }

    public DesignAssetDto ImportAsset(byte[] content, string fileName, string contentType)
    {
        if (content.Length == 0) throw new BusinessRuleException("That file is empty.", "asset_empty");
        var name = Path.GetFileNameWithoutExtension(fileName);
        if (name.Length > 60) name = name[..60];
        var id = "a" + Guid.NewGuid().ToString("n")[..10];

        var looksSvg = fileName.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                       || contentType.Contains("svg", StringComparison.OrdinalIgnoreCase);
        if (looksSvg)
        {
            if (content.Length > DesignCatalog.MaxSvgBytes * 4)
                throw new BusinessRuleException($"That SVG is too big — keep it under {DesignCatalog.MaxSvgBytes / 1024}KB.", "svg_too_large");
            SanitizedSvg svg;
            try { svg = SvgSanitizer.Sanitize(Encoding.UTF8.GetString(content)); }
            catch (SvgRejectedException e) { throw new BusinessRuleException(e.Message, "svg_invalid"); }
            var document = svg.Document;
            var bytes = Encoding.UTF8.GetByteCount(document);
            if (bytes > DesignCatalog.MaxSvgBytes)
                throw new BusinessRuleException(
                    $"That SVG is {bytes / 1024}KB once cleaned up — keep it under {DesignCatalog.MaxSvgBytes / 1024}KB.", "svg_too_large");
            return new DesignAssetDto(id, "svg", document, svg.Width, svg.Height, svg.Colors, name, bytes);
        }

        var type = ImageSniffer.Detect(content);
        if (type is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif"))
            throw new BusinessRuleException("Upload an SVG, PNG, JPEG, WebP or GIF.", "asset_unsupported");

        // Shrink until it fits: a decoration rarely needs more than a phone screen's worth of pixels.
        foreach (var edge in new[] { 1600, 1200, 900, 640, 420 })
        {
            var optimized = optimizer.Optimize(content, type, edge);
            if (optimized.Content.Length > DesignCatalog.MaxImageBytes) continue;
            var data = $"data:{type};base64,{Convert.ToBase64String(optimized.Content)}";
            return new DesignAssetDto(id, "image", data, optimized.Width, optimized.Height, null, name, optimized.Content.Length, TintMask.HasAlpha(optimized.Content));
        }
        throw new BusinessRuleException(
            $"That picture is too big to embed even when shrunk — keep it under {DesignCatalog.MaxImageBytes / 1024}KB.", "image_too_large");
    }

    public DesignAssetDto ShrinkAsset(ShrinkAssetRequest request)
    {
        if (!double.IsFinite(request.Width) || !double.IsFinite(request.Height) || request.Width <= 0 || request.Height <= 0
            || request.Width > 20000 || request.Height > 20000)
            throw new BusinessRuleException("Give the size it's shown at.", "asset_size");
        if (string.IsNullOrEmpty(request.Data) || request.Data.Length > 10 * 1024 * 1024)
            throw new BusinessRuleException("That asset isn't one this design can hold.", "asset_invalid");
        var name = request.Name ?? string.Empty;
        var unchanged = new DesignAssetDto(request.Id, request.Kind, request.Data, 0, 0, null, name, Encoding.UTF8.GetByteCount(request.Data));

        if (request.Kind == "svg")
        {
            SanitizedSvg before;
            try { before = SvgSanitizer.Sanitize(request.Data); }
            catch (SvgRejectedException e) { throw new BusinessRuleException(e.Message, "svg_invalid"); }
            var original = before.Document;
            SanitizedSvg after;
            try { after = SvgSanitizer.Sanitize(SvgShrink.Shrink(original, before.Width, before.Height, request.Width, request.Height)); }
            catch (Exception e) when (e is SvgRejectedException or System.Xml.XmlException) { return unchanged; }
            var bytes = Encoding.UTF8.GetByteCount(after.Document);
            return bytes < Encoding.UTF8.GetByteCount(request.Data)
                ? new DesignAssetDto(request.Id, "svg", after.Document, after.Width, after.Height, after.Colors, name, bytes)
                : unchanged;
        }

        if (request.Kind != "image") throw new BusinessRuleException("Only pictures and illustrations can be shrunk.", "asset_invalid");
        var (content, type) = DecodeImage(request.Data);

        // Twice the size it's shown at: sharp on a 2x screen, which is most phones' and laptops' density.
        // The format stays: turning a PNG into a JPEG would put smudges round lettering and flat colour.
        var edge = (int)Math.Ceiling(2 * Math.Max(request.Width, request.Height));
        var optimized = optimizer.Optimize(content, type, Math.Max(16, edge));
        if (!optimized.Changed || optimized.Content.Length >= content.Length) return unchanged with { Bytes = content.Length };
        return new DesignAssetDto(request.Id, "image", $"data:{optimized.ContentType};base64,{Convert.ToBase64String(optimized.Content)}",
            optimized.Width, optimized.Height, null, name, optimized.Content.Length, TintMask.HasAlpha(optimized.Content));
    }

    public ImageTintInfoDto AnalyseImage(string data)
    {
        if (string.IsNullOrEmpty(data) || data.Length > 10 * 1024 * 1024)
            throw new BusinessRuleException("That picture isn't one this design can hold.", "asset_invalid");
        var (content, _) = DecodeImage(data);
        try
        {
            var (alpha, mask) = TintMask.Analyse(content);
            return new ImageTintInfoDto(alpha, mask is null ? null : "data:image/png;base64," + Convert.ToBase64String(mask));
        }
        catch (Exception e) when (e is NotSupportedException or InvalidImageContentException or UnknownImageFormatException)
        {
            throw new BusinessRuleException("That picture couldn't be read.", "asset_invalid");
        }
    }

    /// <summary>A picture held in a design: a data: URI of a PNG, JPEG, WebP or GIF, checked by its bytes.</summary>
    private static (byte[] Content, string Type) DecodeImage(string data)
    {
        var match = System.Text.RegularExpressions.Regex.Match(data, "^data:(image/(?:png|jpeg|webp|gif));base64,([A-Za-z0-9+/=]+)$");
        if (!match.Success) throw new BusinessRuleException("That picture isn't one this design can hold.", "asset_invalid");
        byte[] content;
        try { content = Convert.FromBase64String(match.Groups[2].Value); }
        catch (FormatException) { throw new BusinessRuleException("That picture isn't one this design can hold.", "asset_invalid"); }
        var type = ImageSniffer.Detect(content);
        if (type is not ("image/png" or "image/jpeg" or "image/webp" or "image/gif")) throw new BusinessRuleException("That picture isn't one this design can hold.", "asset_invalid");
        return (content, type);
    }

    public string ImportDocument(string packagedHtml)
    {
        var manifest = packager.BuildManifest("import", "import", packagedHtml);
        var data = DesignSampleData.Build(new DesignScene(), DesignSampleData.Filled);
        var eventObj = (JsonObject)data["event"]!;

        // Fill every field the template declares, so what gets measured is a filled invitation.
        foreach (var field in manifest.Fields)
        {
            if (!field.Key.StartsWith("event.", StringComparison.Ordinal)) continue;
            var key = field.Key[6..];
            if (key.Contains('.') || eventObj.ContainsKey(key)) continue;
            eventObj[key] = field.Type == "url" ? "https://example.com" : field.Options?.FirstOrDefault() ?? field.Label;
        }
        foreach (var slot in manifest.ImageSlots)
        {
            if (!slot.Key.StartsWith("event.", StringComparison.Ordinal)) continue;
            var key = slot.Key[6..];
            if (key.Contains('.')) continue;
            eventObj[key] = slot.Multiple
                ? new JsonArray(Enumerable.Range(0, Math.Max(4, slot.MinImages ?? 0)).Select(i => (JsonNode?)DesignSampleData.Placeholder(i)).ToArray())
                : DesignSampleData.Placeholder(key.Length);
        }
        data["resolvedBlocks"] = new JsonArray(manifest.ContentBlocks.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray());
        data["guest"]!["role"] = manifest.Roles.FirstOrDefault();

        var bound = ServerBinder.Bind(packagedHtml, data);
        var marker = bound.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        var script = $"<script>{DesignImportScript.Js}</script>";
        return marker < 0 ? bound + script : bound.Insert(marker, script);
    }

    private static DesignScene Parse(string json)
    {
        if (Encoding.UTF8.GetByteCount(json ?? string.Empty) > MaxSceneBytes)
            throw new BusinessRuleException(
                $"This design is over {MaxSceneBytes / 1024 / 1024}MB — use smaller pictures.", "scene_too_large");
        try { return DesignScene.Parse(json!); }
        catch (DesignSceneException e) { throw new BusinessRuleException(e.Message, "scene_invalid"); }
    }

    /// <summary>
    /// A fixed colour on forty elements is one thing to fix, not forty: repeated warnings of the same kind
    /// collapse into one that says how many there are. Errors are never collapsed.
    /// </summary>
    private static IReadOnlyList<DesignIssueDto> Collapse(IEnumerable<DesignIssue> issues)
    {
        var list = new List<DesignIssueDto>();
        foreach (var group in issues.GroupBy(i => (i.Severity, i.Code)))
        {
            var items = group.ToList();
            if (group.Key.Severity == DesignIssue.Error || items.Count <= 3)
            {
                list.AddRange(items.Select(i => new DesignIssueDto(i.Severity, i.Code, i.Message, i.ElementId)));
                continue;
            }
            list.Add(new DesignIssueDto(items[0].Severity, items[0].Code,
                $"{items[0].Message} (and {items.Count - 1} more like it)", items[0].ElementId));
        }
        return list.OrderBy(i => i.Severity == DesignIssue.Error ? 0 : 1).ToList();
    }
}
