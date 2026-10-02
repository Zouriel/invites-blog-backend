using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Infrastructure.Images;
using InvitesBlog.Infrastructure.Templates;
using InvitesBlog.Infrastructure.Templates.Art;
using InvitesBlog.TemplateCompiler.Design;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Gif;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace InvitesBlog.Tests;

public class ArtLibraryTests
{
    private static readonly IConfiguration Config =
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Urls:AssetsBase"] = "/assets" })
            .AddEnvironmentVariables().Build(); // ArtLibrary__FreeSvg__Email etc. for the live tests

    private static DesignEngine Engine() => new(
        new RawTemplatePackager(Substitute.For<IStorageService>()),
        new ImageSharpOptimizer(NullLogger<ImageSharpOptimizer>.Instance),
        Config);

    private static ArtLibrary Library(IEnumerable<IArtSource>? sources = null) =>
        new(sources ?? [], Engine(), NullLogger<ArtLibrary>.Instance);

    /// <summary>
    /// Places an import the way the editor does — a group of full-box layers with keyframes on a track —
    /// and returns what Check says, so the contract between the importer and the compiler is tested.
    /// </summary>
    private static IReadOnlyList<DesignIssueDto> Place(ArtImportDto art)
    {
        const double w = 280;
        var h = Math.Round(w * art.Height / art.Width, 1);
        var k = w / art.Width;
        var children = new JsonArray();
        var i = 0;
        foreach (var layer in art.Layers)
        {
            var asset = art.Assets.Single(a => a.Id == layer.Asset);
            var el = new JsonObject
            {
                ["id"] = $"l{i++}", ["type"] = asset.Kind, ["x"] = 0, ["y"] = 0, ["w"] = w, ["h"] = h,
                ["keyframes"] = new JsonArray(layer.Frames.Select(f => (JsonNode)new JsonObject
                {
                    ["t"] = f.T, ["x"] = f.Dx * k, ["y"] = f.Dy * k, ["rotate"] = f.Rotate, ["scale"] = f.Scale, ["opacity"] = f.Opacity,
                }).ToArray()),
            };
            if (layer.Frames.Count > 0) el["track"] = new JsonObject { ["start"] = 100, ["end"] = 1300 };
            el[asset.Kind] = asset.Kind == "svg"
                ? new JsonObject { ["asset"] = asset.Id, ["fills"] = new JsonObject() }
                : new JsonObject { ["asset"] = asset.Id, ["fit"] = "contain", ["radius"] = 0 };
            children.Add(el);
        }
        var scene = JsonNode.Parse(DesignStarters.Create("blank")!.ToJson())!.AsObject();
        scene["elements"] = new JsonArray(
            new JsonObject { ["id"] = "art", ["type"] = "group", ["x"] = 55, ["y"] = 300, ["w"] = w, ["h"] = h, ["children"] = children },
            new JsonObject { ["id"] = "rsvp", ["type"] = "rsvp", ["x"] = 85, ["y"] = 900, ["w"] = 220, ["h"] = 54 });
        var assets = new JsonObject();
        foreach (var a in art.Assets)
            assets[a.Id] = new JsonObject { ["kind"] = a.Kind, ["data"] = a.Data, ["width"] = a.Width, ["height"] = a.Height };
        scene["assets"] = assets;
        return Engine().Build(scene.ToJsonString(), new DesignBuildOptions()).Issues;
    }

    [Fact]
    public void An_uploaded_animated_svg_comes_back_as_layers_that_pass_check()
    {
        var svg = """
            <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 200 200">
              <style>.sun{fill:#f5b400} @keyframes pulse{50%{opacity:.4}} .glow{animation:pulse 2s ease-in-out infinite}</style>
              <circle class="glow" cx="100" cy="100" r="90" fill="#ffe8a0"/>
              <g><circle class="sun" cx="100" cy="100" r="50"/>
                <animateTransform attributeName="transform" type="rotate" from="0 100 100" to="360 100 100" dur="4s" repeatCount="indefinite"/></g>
            </svg>
            """;
        var art = Library().ImportFile(Encoding.UTF8.GetBytes(svg), "sun.svg", "image/svg+xml");
        Assert.True(art.Animated);
        Assert.Equal(2, art.Layers.Count);
        Assert.All(art.Layers, l => Assert.NotEmpty(l.Frames));
        Assert.Contains("#f5b400", art.Assets[1].Colors!);
        var errors = Place(art).Where(x => x.Severity == "error").ToList();
        Assert.True(errors.Count == 0, string.Join("; ", errors.Select(e => e.Code + ": " + e.Message)));
    }

    [Fact]
    public void An_animated_gif_becomes_a_flipbook_of_webp_stills()
    {
        using var gif = new Image<Rgba32>(40, 20, new Rgba32(255, 0, 0));
        foreach (var colour in new[] { new Rgba32(0, 255, 0), new Rgba32(0, 0, 255) })
        {
            using var frame = new Image<Rgba32>(40, 20, colour);
            gif.Frames.AddFrame(frame.Frames.RootFrame);
        }
        foreach (var f in gif.Frames) f.Metadata.GetGifMetadata().FrameDelay = 50;
        using var ms = new MemoryStream();
        gif.SaveAsGif(ms);

        var art = Library().ImportFile(ms.ToArray(), "blink.gif", "image/gif");
        Assert.True(art.Animated);
        Assert.Equal(3, art.Layers.Count);
        Assert.Equal(2, art.Loops); // 1.5s a play, about three seconds' worth
        Assert.All(art.Assets, a => Assert.StartsWith("data:image/webp;base64,", a.Data));
        Assert.Equal(1, art.Layers[0].Frames[0].Opacity);
        Assert.Equal(0, art.Layers[1].Frames[0].Opacity);
        Assert.Equal(1, art.Layers[2].Frames[^1].Opacity);
        Assert.DoesNotContain(Place(art), x => x.Severity == "error");
    }

    [Fact]
    public void A_still_picture_is_one_layer()
    {
        using var png = new Image<Rgba32>(30, 30, new Rgba32(10, 20, 30));
        using var ms = new MemoryStream();
        png.SaveAsPng(ms);
        var art = Library().ImportFile(ms.ToArray(), "dot.png", "image/png");
        Assert.False(art.Animated);
        Assert.Empty(Assert.Single(art.Layers).Frames);
    }

    [Fact]
    public void A_padded_rendering_is_trimmed_to_what_it_draws()
    {
        using var png = new Image<Rgba32>(60, 60, new Rgba32(0, 0, 0, 0));
        for (var y = 20; y < 40; y++) for (var x = 0; x < 60; x++) png[x, y] = new Rgba32(200, 0, 0, 255);
        using var ms = new MemoryStream();
        png.SaveAsPng(ms);
        using var trimmed = Image.Load(ArtLibrary.TrimTransparent(ms.ToArray()));
        Assert.Equal((60, 20), (trimmed.Width, trimmed.Height));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.20.0.5", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("198.35.26.112", true)]
    [InlineData("2606:4700::1111", true)]
    public void Only_public_addresses_are_downloaded_from(string address, bool allowed) =>
        Assert.Equal(allowed, ArtHttp.IsPublic(IPAddress.Parse(address)));

    [Theory]
    [InlineData("https://upload.wikimedia.org/wikipedia/commons/8/83/Mature_flower_diagram-pt.svg",
        "https://upload.wikimedia.org/wikipedia/commons/thumb/8/83/Mature_flower_diagram-pt.svg/250px-Mature_flower_diagram-pt.svg.png")]
    [InlineData("https://upload.wikimedia.org/wikipedia/commons/3/38/Sticker.gif",
        "https://upload.wikimedia.org/wikipedia/commons/thumb/3/38/Sticker.gif/250px-Sticker.gif")]
    [InlineData("https://live.staticflickr.com/1/2.jpg", null)]
    public void Commons_files_get_wikimedias_own_preview(string url, string? thumb) =>
        Assert.Equal(thumb, OpenverseSource.WikimediaThumb(url));

    [Fact]
    public async Task A_library_file_on_a_private_address_is_refused()
    {
        using var http = new HttpClient(ArtHttp.Handler());
        var e = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            ArtHttp.DownloadAsync(http, new Uri("https://127.0.0.1/x.svg"), 1000, CancellationToken.None));
        Assert.Equal("art_unavailable", e.ErrorCode);
    }

    // ----- Live: only with ART_LIVE=1, since they reach the real libraries -------------------------

    private static (ArtLibrary Library, ServiceProvider Services)? Live()
    {
        if (Environment.GetEnvironmentVariable("ART_LIVE") != "1") return null;
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Config);
        services.AddHttpClient(ArtHttp.ClientName, c => c.DefaultRequestHeaders.UserAgent.ParseAdd(ArtHttp.UserAgent))
            .ConfigurePrimaryHttpMessageHandler(ArtHttp.Handler);
        services.AddSingleton<IArtSource, OpenverseSource>();
        services.AddSingleton<IArtSource, OpenClipartSource>();
        services.AddSingleton<IArtSource, FreeSvgSource>();
        var sp = services.BuildServiceProvider();
        return (Library(sp.GetServices<IArtSource>()), sp);
    }

    /// <summary>ART_IDS=source:id,… — imports exactly those, logging how each came in.</summary>
    [Fact]
    public async Task Live_import_named_items()
    {
        var ids = Environment.GetEnvironmentVariable("ART_IDS");
        if (string.IsNullOrEmpty(ids) || Live() is not var (library, sp)) return;
        using var _ = sp;
        var log = new StringBuilder();
        foreach (var pair in ids.Split(','))
        {
            var (source, id) = (pair.Split(':')[0], pair.Split(':')[1]);
            try
            {
                var art = await library.ImportAsync(new ArtImportRequest(source, id, null));
                log.AppendLine($"{pair}: {(art.AsPicture ? "picture" : "vector")} {art.Width}x{art.Height}, {art.Layers.Count} layers, {art.Bytes / 1024}KB");
                Assert.DoesNotContain(Place(art), x => x.Severity == "error");
            }
            catch (AppException e) { log.AppendLine($"{pair}: {e.ErrorCode} {e.Message}"); }
            await Task.Delay(3000);
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "art-ids.log"), log.ToString());
    }

    [Theory]
    [InlineData("openverse", "vector", "flower")]
    [InlineData("openverse", "animated", "flower")]
    [InlineData("openverse", "picture", "wedding")]
    [InlineData("openclipart", "vector", "rose")]
    [InlineData("freesvg", "vector", "rose")]
    [InlineData("freesvg", "vector", "floral frame")]
    [InlineData("freesvg", "vector", "wedding")]
    public async Task Live_search_and_import(string source, string kind, string query)
    {
        if (Live() is not var (library, sp)) return;
        using var _ = sp;
        var results = await library.SearchAsync(source, query, kind, 1);
        Assert.NotEmpty(results.Items);
        var log = new StringBuilder($"{source}/{kind}/{query}: {results.Total} results\n");
        var imported = 0;
        var take = int.TryParse(Environment.GetEnvironmentVariable("ART_TAKE"), out var n) ? n : 4;
        foreach (var item in results.Items.Where(i => !i.TooLarge).Take(take))
        {
            try
            {
                var art = await library.ImportAsync(new ArtImportRequest(source, item.Id, item.Title));
                log.AppendLine($"  ok {item.Id} {item.Title}: {art.Layers.Count} layers, animated={art.Animated}, {art.Bytes / 1024}KB");
                Assert.DoesNotContain(Place(art), x => x.Severity == "error");
                imported++;
            }
            catch (AppException e)
            {
                log.AppendLine($"  refused {item.Id} {item.Title}: {e.ErrorCode} {e.Message}");
            }
        }
        File.AppendAllText(Path.Combine(Path.GetTempPath(), "art-live.log"), log.ToString());
        Assert.True(imported > 0, log.ToString());
    }
}

public class ArtFileProbe
{
    /// <summary>ART_DIR=folder of real SVG/GIF files: imports each and logs what came of it. Not run otherwise.</summary>
    [Fact]
    public void Import_every_file_in_ART_DIR()
    {
        var dir = Environment.GetEnvironmentVariable("ART_DIR");
        if (string.IsNullOrEmpty(dir)) return;
        var engine = new DesignEngine(new RawTemplatePackager(Substitute.For<IStorageService>()),
            new ImageSharpOptimizer(NullLogger<ImageSharpOptimizer>.Instance),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Urls:AssetsBase"] = "/assets" }).Build());
        var library = new ArtLibrary([], engine, NullLogger<ArtLibrary>.Instance);
        var log = new StringBuilder();
        foreach (var file in Directory.GetFiles(dir).Where(f => f.EndsWith(".svg") || f.EndsWith(".gif")).Order())
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var art = library.ImportFile(File.ReadAllBytes(file), Path.GetFileName(file), "");
                log.AppendLine($"{Path.GetFileName(file)} {new FileInfo(file).Length / 1024}KB → {art.Layers.Count} layers, {art.Bytes / 1024}KB, largest {art.Assets.Max(a => a.Bytes) / 1024}KB ({sw.ElapsedMilliseconds}ms)");
            }
            catch (AppException e)
            {
                log.AppendLine($"{Path.GetFileName(file)} {new FileInfo(file).Length / 1024}KB → {e.ErrorCode}: {e.Message} ({sw.ElapsedMilliseconds}ms)");
            }
        }
        foreach (var file in Directory.GetFiles(dir).Where(f => f.EndsWith(".svg")).Order())
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                var plan = InvitesBlog.TemplateCompiler.Design.Art.SvgArtConverter.Convert(File.ReadAllText(file), 8, 0, 150 * 1024);
                var convert = sw.ElapsedMilliseconds;
                var clean = plan.Layers.Sum(l => SvgSanitizer.Sanitize(l.Svg).Document.Length);
                log.AppendLine($"  timing {Path.GetFileName(file)}: convert {convert}ms, sanitize {sw.ElapsedMilliseconds - convert}ms, raw {plan.Layers.Sum(l => l.Svg.Length) / 1024}KB → clean {clean / 1024}KB in {plan.Layers.Count}");
            }
            catch (Exception e) { log.AppendLine($"  timing {Path.GetFileName(file)}: {e.GetType().Name} {e.Message}"); }
        }
        File.WriteAllText(Path.Combine(dir, "result.log"), log.ToString());
    }
}
