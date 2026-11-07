using System.Text.Json.Nodes;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Templates.Art;

/// <summary>
/// Pixabay (pixabay.com/api): illustrations and vectors under the Pixabay Content License: free, no
/// credit needed, may be changed and used in new works, but not handed on as-is ("standalone").
///
/// <para><b>Settings.</b> <c>ArtLibrary:Pixabay:ApiKey</c> connects it. Until Pixabay approves our use
/// (requested 2026-11-07) it is only shown to the accounts in <c>ArtLibrary:Pixabay:AllowedUserIds</c>;
/// <c>ArtLibrary:Pixabay:Public=true</c> shows it to everyone. <c>ArtLibrary:Pixabay:FullAccess=true</c>
/// (once Pixabay grants "full API access") brings the original files, and SVG where there is one;
/// without it imports are their 1280px picture.</para>
///
/// <para><b>Their rules.</b> Search results are cached for 24 hours; their image links are only used to
/// show results, and files are downloaded to us, never linked; results say they're from Pixabay. The key
/// travels in the query string, so this source has its own HTTP client with request logging off.</para>
/// </summary>
public sealed class PixabaySource(IHttpClientFactory http, IConfiguration config, ILogger<PixabaySource> logger)
    : CachedArtSource(http, logger)
{
    public const string ClientName = "art-pixabay";
    private const string Api = "https://pixabay.com/api/";
    public const string License = "Pixabay Content License";

    /// <summary>Its HTTP client: the key is in every URL, so request logging is off.</summary>
    public static IHttpClientBuilder AddClient(IServiceCollection services) =>
        services.AddHttpClient(ClientName, c =>
            {
                c.Timeout = TimeSpan.FromSeconds(120);
                c.DefaultRequestHeaders.UserAgent.ParseAdd(ArtHttp.UserAgent);
            })
            .RemoveAllLoggers();

    public override string Id => "pixabay";

    private string? Key => config["ArtLibrary:Pixabay:ApiKey"] is { Length: > 0 } k ? k : null;
    private bool FullAccess => string.Equals(config["ArtLibrary:Pixabay:FullAccess"], "true", StringComparison.OrdinalIgnoreCase);

    protected override TimeSpan CacheFor => TimeSpan.FromHours(24);
    protected override HttpClient Http => base.Factory.CreateClient(ClientName);

    /// <summary>Everyone once approved; before that, only the listed accounts.</summary>
    public bool VisibleTo(Guid? userId)
    {
        if (string.Equals(config["ArtLibrary:Pixabay:Public"], "true", StringComparison.OrdinalIgnoreCase)) return true;
        return userId is { } id && (config["ArtLibrary:Pixabay:AllowedUserIds"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(s => Guid.TryParse(s, out var g) && g == id);
    }

    public override ArtSourceDto Describe() =>
        new(Id, "Pixabay", Key is not null, ["vector", "picture"], "Illustrations and vectors from Pixabay, free to use without credit");

    protected override async Task<ArtSearchResultDto> SearchUncachedAsync(string query, string kind, int page, CancellationToken ct)
    {
        var key = Key ?? throw new BusinessRuleException("Pixabay isn't connected yet.", "art_unavailable");
        var args = new Dictionary<string, string>
        {
            ["key"] = key, ["q"] = query, ["page"] = page.ToString(), ["per_page"] = "20", ["safesearch"] = "true",
            ["image_type"] = kind == "vector" ? "vector" : "illustration",
        };
        var json = await GetJsonAsync(Url(args), ct);
        var items = new List<ArtItemDto>();
        foreach (var r in json["hits"]?.AsArray() ?? [])
        {
            if (r is null || (long?)r["id"] is not { } id) continue;
            items.Add(new ArtItemDto(Id, id.ToString(), TitleOf(r),
                (string?)r["webformatURL"] ?? (string?)r["previewURL"] ?? "",
                kind == "vector" && FullAccess ? "svg" : "image",
                Clean((string?)r["user"], 60) is { Length: > 0 } user ? user : null,
                License, (string?)r["pageURL"], (int?)r["imageWidth"], (int?)r["imageHeight"], false));
        }
        var total = (int?)json["totalHits"] ?? items.Count;
        return new ArtSearchResultDto(items, page, page * 20 < total, total);
    }

    public override async Task<ArtFile> FetchAsync(string id, string? title, CancellationToken ct)
    {
        var hit = await HitAsync(id, ct);
        // The original (and its SVG, if it has one) needs full API access; until then, the 1280px picture.
        var vector = FullAccess ? (string?)hit["vectorURL"] : null;
        var file = vector is not null && vector.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? vector
            : (FullAccess ? (string?)hit["imageURL"] : null) ?? (string?)hit["largeImageURL"];
        if (!Uri.TryCreate(file, UriKind.Absolute, out var url)) throw new NotFoundException("That picture's file isn't available.", "art_not_found");
        var (content, type) = await ArtHttp.DownloadAsync(Http, url, MaxDownloadBytes, ct);
        return new ArtFile(content, type, TitleOf(hit),
            new ArtCreditDto("Pixabay", Clean((string?)hit["user"], 60) is { Length: > 0 } user ? user : null, License, (string?)hit["pageURL"]));
    }

    public override async Task<(byte[] Content, string ContentType)?> FetchPictureAsync(string id, CancellationToken ct)
    {
        var hit = await HitAsync(id, ct);
        return Uri.TryCreate((string?)hit["largeImageURL"], UriKind.Absolute, out var url)
            ? await ArtHttp.DownloadAsync(Http, url, MaxDownloadBytes, ct)
            : null;
    }

    private async Task<JsonNode> HitAsync(string id, CancellationToken ct)
    {
        var key = Key ?? throw new BusinessRuleException("Pixabay isn't connected yet.", "art_unavailable");
        if (!long.TryParse(id, out var n) || n <= 0) throw new NotFoundException("That picture isn't in the library.", "art_not_found");
        var json = await GetJsonAsync(Url(new() { ["key"] = key, ["id"] = n.ToString() }), ct);
        return json["hits"]?.AsArray().FirstOrDefault() ?? throw new NotFoundException("That picture isn't in the library any more.", "art_not_found");
    }

    private static Uri Url(Dictionary<string, string> args) =>
        new(Api + "?" + string.Join('&', args.Select(a => $"{a.Key}={Uri.EscapeDataString(a.Value)}")));

    /// <summary>Pixabay has no titles; its tags ("flowers, roses, wreath") name the picture.</summary>
    private static string TitleOf(JsonNode hit)
    {
        var tags = ((string?)hit["tags"] ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var name = string.Join(", ", tags.Take(3));
        return Clean(name, 60) is { Length: > 0 } t ? char.ToUpperInvariant(t[0]) + t[1..] : "Illustration";
    }
}
