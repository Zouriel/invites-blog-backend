using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Templates.Art;

/// <summary>A file fetched from a library, with what we know about it.</summary>
public sealed record ArtFile(byte[] Content, string ContentType, string Title, ArtCreditDto Credit);

/// <summary>One free illustration library.</summary>
public interface IArtSource
{
    string Id { get; }
    ArtSourceDto Describe();
    Task<ArtSearchResultDto> SearchAsync(string query, string kind, int page, CancellationToken ct);
    Task<ArtFile> FetchAsync(string id, string? title, CancellationToken ct);
}

/// <summary>Search results are cached briefly: typing, paging back and a second designer searching "roses" all hit the same pages.</summary>
public abstract class CachedArtSource(IHttpClientFactory http, ILogger logger) : IArtSource
{
    public const int MaxDownloadBytes = 12 * 1024 * 1024;
    private static readonly ConcurrentDictionary<string, (DateTimeOffset At, ArtSearchResultDto Result)> Cache = new();

    protected HttpClient Http => http.CreateClient(ArtHttp.ClientName);
    protected ILogger Logger => logger;

    public abstract string Id { get; }
    public abstract ArtSourceDto Describe();
    public abstract Task<ArtFile> FetchAsync(string id, string? title, CancellationToken ct);
    protected abstract Task<ArtSearchResultDto> SearchUncachedAsync(string query, string kind, int page, CancellationToken ct);

    public async Task<ArtSearchResultDto> SearchAsync(string query, string kind, int page, CancellationToken ct)
    {
        var key = $"{Id}|{kind}|{page}|{query.ToLowerInvariant()}";
        if (Cache.TryGetValue(key, out var hit) && DateTimeOffset.UtcNow - hit.At < TimeSpan.FromMinutes(15)) return hit.Result;
        ArtSearchResultDto result;
        try
        {
            result = await SearchUncachedAsync(query, kind, page, ct);
        }
        catch (AppException) { throw; }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(e, "Art search failed at {Source}", Id);
            throw new BusinessRuleException($"{Describe().Name} didn't answer — try again in a moment.", "art_unavailable");
        }
        if (Cache.Count > 2000) Cache.Clear();
        Cache[key] = (DateTimeOffset.UtcNow, result);
        return result;
    }

    protected async Task<JsonNode> GetJsonAsync(Uri url, CancellationToken ct, string? bearer = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var response = await Http.SendAsync(request, ct);
        if (response.StatusCode == HttpStatusCode.TooManyRequests)
            throw new BusinessRuleException($"{Describe().Name} is busy — try again in a minute.", "art_busy");
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw new NotFoundException("That picture isn't in the library any more.", "art_not_found");
        response.EnsureSuccessStatusCode();
        return JsonNode.Parse(await response.Content.ReadAsStringAsync(ct)) ?? new JsonObject();
    }

    protected static string Clean(string? text, int max = 80)
    {
        var t = Regex.Replace(WebUtility.HtmlDecode(text ?? string.Empty), @"\s+", " ").Trim();
        return t.Length > max ? t[..max].TrimEnd() : t;
    }
}

/// <summary>
/// Openverse (api.openverse.org): millions of openly licensed images, filtered to CC0 and public-domain
/// marks. Works anonymously, with a small daily quota per server; set
/// <c>ArtLibrary:Openverse:ClientId</c>/<c>ClientSecret</c> (registered at /v1/auth_tokens/register/)
/// for the larger one.
/// </summary>
public sealed class OpenverseSource(IHttpClientFactory http, IConfiguration config, ILogger<OpenverseSource> logger)
    : CachedArtSource(http, logger)
{
    private const string Api = "https://api.openverse.org/v1/";
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static (string Token, DateTimeOffset Expires)? token;

    public override string Id => "openverse";

    public override ArtSourceDto Describe() =>
        new(Id, "Openverse", true, ["vector", "animated", "picture"], "Public-domain images from Wikimedia, museums and more");

    private async Task<string?> TokenAsync(CancellationToken ct)
    {
        var id = config["ArtLibrary:Openverse:ClientId"];
        var secret = config["ArtLibrary:Openverse:ClientSecret"];
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(secret)) return null;
        if (token is { } t && t.Expires > DateTimeOffset.UtcNow.AddMinutes(5)) return t.Token;
        await TokenLock.WaitAsync(ct);
        try
        {
            if (token is { } again && again.Expires > DateTimeOffset.UtcNow.AddMinutes(5)) return again.Token;
            using var response = await Http.PostAsync(new Uri(Api + "auth_tokens/token/"), new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials", ["client_id"] = id, ["client_secret"] = secret,
            }), ct);
            if (!response.IsSuccessStatusCode)
            {
                Logger.LogWarning("Openverse token refused ({Status}); searching anonymously", (int)response.StatusCode);
                return null;
            }
            var body = await response.Content.ReadFromJsonAsync<JsonObject>(ct);
            var access = (string?)body?["access_token"];
            if (access is null) return null;
            var seconds = (double?)body?["expires_in"] ?? 3600;
            token = (access, DateTimeOffset.UtcNow.AddSeconds(seconds));
            return access;
        }
        catch (Exception e) when (e is HttpRequestException or JsonException)
        {
            Logger.LogWarning(e, "Openverse token failed; searching anonymously");
            return null;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    protected override async Task<ArtSearchResultDto> SearchUncachedAsync(string query, string kind, int page, CancellationToken ct)
    {
        var args = new Dictionary<string, string>
        {
            ["q"] = query, ["page"] = page.ToString(), ["page_size"] = "20", ["license"] = "cc0,pdm", ["mature"] = "false",
        };
        switch (kind)
        {
            case "animated": args["extension"] = "gif"; break;
            case "picture": args["category"] = "illustration,digitized_artwork"; args["extension"] = "png,jpg,jpeg,webp"; break;
            default: args["extension"] = "svg"; break;
        }
        var url = new Uri(Api + "images/?" + string.Join('&', args.Select(a => $"{a.Key}={Uri.EscapeDataString(a.Value)}")));
        var json = await GetJsonAsync(url, ct, await TokenAsync(ct));
        var items = new List<ArtItemDto>();
        foreach (var r in json["results"]?.AsArray() ?? [])
        {
            if (r is null) continue;
            var filetype = ((string?)r["filetype"] ?? "").ToLowerInvariant();
            var id = (string?)r["id"];
            if (id is null) continue;
            var size = (long?)r["filesize"];
            items.Add(new ArtItemDto(Id, id, Clean((string?)r["title"]) is { Length: > 0 } title ? title : "Untitled",
                WikimediaThumb((string?)r["url"]) ?? (string?)r["thumbnail"] ?? $"{Api}images/{id}/thumb/",
                filetype == "svg" ? "svg" : filetype == "gif" ? "gif" : "image",
                Clean((string?)r["creator"], 60) is { Length: > 0 } c ? c : null,
                LicenseLabel((string?)r["license"]), (string?)r["foreign_landing_url"],
                (int?)r["width"], (int?)r["height"], size > MaxDownloadBytes));
        }
        var pageCount = (int?)json["page_count"] ?? page;
        return new ArtSearchResultDto(items, page, page < pageCount, (int?)json["result_count"]);
    }

    private static string LicenseLabel(string? license) => license == "pdm" ? "Public domain" : "CC0";

    /// <summary>
    /// Wikimedia's own preview for a Commons file. Openverse can't thumbnail an SVG (it answers 424), and
    /// most of its SVGs are on Commons, which renders any file as a PNG at its standard widths — 250px
    /// is one; other widths are refused.
    /// </summary>
    public static string? WikimediaThumb(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Host != "upload.wikimedia.org") return null;
        var parts = uri.AbsolutePath.Split('/');
        // /wikipedia/commons/a/ab/Name.ext
        if (parts.Length != 6 || parts[1] != "wikipedia" || parts[3].Length != 1 || parts[4].Length != 2) return null;
        var name = parts[5];
        var png = name.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
                  || name.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase) ? ".png" : "";
        return $"https://upload.wikimedia.org/wikipedia/{parts[2]}/thumb/{parts[3]}/{parts[4]}/{name}/250px-{name}{png}";
    }

    public override async Task<ArtFile> FetchAsync(string id, string? title, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var guid)) throw new NotFoundException("That picture isn't in the library.", "art_not_found");
        var detail = await GetJsonAsync(new Uri($"{Api}images/{guid}/"), ct, await TokenAsync(ct));
        // Checked again here, not trusted from the search: only work nobody has to be credited for.
        var license = (string?)detail["license"];
        if (license is not ("cc0" or "pdm"))
            throw new BusinessRuleException("That picture's licence needs a credit, so it can't go into a template.", "art_license");
        if (!Uri.TryCreate((string?)detail["url"], UriKind.Absolute, out var url))
            throw new NotFoundException("That picture's file isn't available.", "art_not_found");
        var (content, type) = await ArtHttp.DownloadAsync(Http, url, MaxDownloadBytes, ct);
        var name = Clean((string?)detail["title"], 60);
        return new ArtFile(content, type, name.Length > 0 ? name : "Picture",
            new ArtCreditDto("Openverse", Clean((string?)detail["creator"], 60) is { Length: > 0 } c ? c : null,
                LicenseLabel(license), (string?)detail["foreign_landing_url"]));
    }
}

/// <summary>
/// Openclipart (openclipart.org): ~180,000 public-domain SVGs. It has no search API any more, so the
/// search page is read for ids and names; files come from its stable <c>/download/{id}</c> URLs.
/// </summary>
public sealed partial class OpenClipartSource(IHttpClientFactory http, ILogger<OpenClipartSource> logger)
    : CachedArtSource(http, logger)
{
    private const string Site = "https://openclipart.org";

    [GeneratedRegex(@"<a href=""/detail/(\d+)/[^""]*"">\s*<img src=""/image/\d+px/\d+"" alt=""([^""]*)""")]
    private static partial Regex ItemRegex();

    [GeneratedRegex(@"([\d,]+) clipart for")]
    private static partial Regex TotalRegex();

    [GeneratedRegex(@"Page (\d+) of (\d+)")]
    private static partial Regex PagesRegex();

    public override string Id => "openclipart";

    public override ArtSourceDto Describe() => new(Id, "Openclipart", true, ["vector"], "Public-domain clip art, all SVG");

    protected override async Task<ArtSearchResultDto> SearchUncachedAsync(string query, string kind, int page, CancellationToken ct)
    {
        if (kind != "vector") return new ArtSearchResultDto([], page, false, 0);
        var url = new Uri($"{Site}/search/?query={Uri.EscapeDataString(query)}&p={page}");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using var response = await Http.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();
        var html = await response.Content.ReadAsStringAsync(ct);
        var items = ItemRegex().Matches(html).Select(m => new ArtItemDto(
            Id, m.Groups[1].Value, Clean(m.Groups[2].Value) is { Length: > 0 } t ? t : "Clip art",
            $"{Site}/image/200px/{m.Groups[1].Value}", "svg", null, "Public domain",
            $"{Site}/detail/{m.Groups[1].Value}", null, null, false)).ToList();
        var pages = PagesRegex().Match(html);
        var hasMore = pages.Success && int.Parse(pages.Groups[1].Value) < int.Parse(pages.Groups[2].Value);
        var total = TotalRegex().Match(html) is { Success: true } tm && int.TryParse(tm.Groups[1].Value.Replace(",", ""), out var n) ? n : (int?)null;
        return new ArtSearchResultDto(items, page, hasMore, total);
    }

    public override async Task<ArtFile> FetchAsync(string id, string? title, CancellationToken ct)
    {
        if (!long.TryParse(id, out var number) || number <= 0) throw new NotFoundException("That picture isn't in the library.", "art_not_found");
        var (content, type) = await ArtHttp.DownloadAsync(Http, new Uri($"{Site}/download/{number}"), MaxDownloadBytes, ct);
        var name = Clean(title, 60);
        return new ArtFile(content, type, name.Length > 0 ? name : "Clip art",
            new ArtCreditDto("Openclipart", null, "Public domain", $"{Site}/detail/{number}"));
    }
}

/// <summary>
/// FreeSVG (freesvg.org): CC0 SVGs, API for registered users only — set <c>ArtLibrary:FreeSvg:Email</c>
/// and <c>Password</c> for the account the server signs in with. Until then it is listed as unavailable.
///
/// <para>The API's response shape isn't documented beyond its routes, so items are read loosely: an
/// id, a title, and whichever fields hold a preview and the SVG.</para>
/// </summary>
public sealed class FreeSvgSource(IHttpClientFactory http, IConfiguration config, ILogger<FreeSvgSource> logger)
    : CachedArtSource(http, logger)
{
    private const string Site = "https://freesvg.org";
    private static readonly SemaphoreSlim TokenLock = new(1, 1);
    private static (string Token, DateTimeOffset Expires)? token;

    public override string Id => "freesvg";

    private bool Configured =>
        !string.IsNullOrWhiteSpace(config["ArtLibrary:FreeSvg:Email"]) && !string.IsNullOrWhiteSpace(config["ArtLibrary:FreeSvg:Password"]);

    public override ArtSourceDto Describe() =>
        new(Id, "FreeSVG", Configured, ["vector"], Configured ? "Public-domain SVGs" : "Needs the server's FreeSVG account");

    private async Task<string> TokenAsync(CancellationToken ct)
    {
        if (!Configured) throw new BusinessRuleException("FreeSVG isn't connected yet.", "art_unavailable");
        if (token is { } t && t.Expires > DateTimeOffset.UtcNow) return t.Token;
        await TokenLock.WaitAsync(ct);
        try
        {
            if (token is { } again && again.Expires > DateTimeOffset.UtcNow) return again.Token;
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri($"{Site}/api/v1/auth/login"))
            {
                Content = JsonContent.Create(new { email = config["ArtLibrary:FreeSvg:Email"], password = config["ArtLibrary:FreeSvg:Password"] }),
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await Http.SendAsync(request, ct);
            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));
            var access = (string?)body?["token"];
            if (!response.IsSuccessStatusCode || access is null)
            {
                Logger.LogWarning("FreeSVG sign-in refused ({Status})", (int)response.StatusCode);
                throw new BusinessRuleException("FreeSVG refused the server's sign-in.", "art_unavailable");
            }
            token = (access, DateTimeOffset.UtcNow.AddHours(12));
            return access;
        }
        finally
        {
            TokenLock.Release();
        }
    }

    protected override async Task<ArtSearchResultDto> SearchUncachedAsync(string query, string kind, int page, CancellationToken ct)
    {
        if (kind != "vector") return new ArtSearchResultDto([], page, false, 0);
        var path = string.IsNullOrWhiteSpace(query) ? $"svgs?page={page}" : $"search?query={Uri.EscapeDataString(query)}&page={page}";
        var json = await GetJsonAsync(new Uri($"{Site}/api/v1/{path}"), ct, await TokenAsync(ct));
        var list = (json as JsonArray) ?? json["data"] as JsonArray ?? json["svgs"] as JsonArray ?? json["results"] as JsonArray ?? [];
        var items = new List<ArtItemDto>();
        foreach (var node in list)
        {
            if (node is not JsonObject o) continue;
            var id = o["id"]?.ToString();
            if (string.IsNullOrEmpty(id)) continue;
            var thumb = FindUrl(o, "thumb", "preview", "png", "image");
            if (thumb is null) continue;
            items.Add(new ArtItemDto(Id, id, Clean((string?)o["title"] ?? (string?)o["name"]) is { Length: > 0 } t ? t : "SVG",
                thumb, "svg", null, "CC0", $"{Site}/{Clean((string?)o["slug"])}", null, null, false));
        }
        var last = (int?)json["last_page"] ?? (int?)json["meta"]?["last_page"];
        var hasMore = last is { } l ? page < l : items.Count >= 25;
        return new ArtSearchResultDto(items, page, hasMore, (int?)json["total"] ?? (int?)json["meta"]?["total"]);
    }

    public override async Task<ArtFile> FetchAsync(string id, string? title, CancellationToken ct)
    {
        if (!long.TryParse(id, out var number) || number <= 0) throw new NotFoundException("That picture isn't in the library.", "art_not_found");
        var bearer = await TokenAsync(ct);
        var json = await GetJsonAsync(new Uri($"{Site}/api/v1/svg/{number}"), ct, bearer);
        var o = (json["data"] as JsonObject) ?? (json as JsonObject) ?? new JsonObject();
        var file = FindUrl(o, "svg", "file", "download", "url")
                   ?? throw new NotFoundException("That picture's file isn't available.", "art_not_found");
        var url = new Uri(file);
        if (!url.Host.EndsWith("freesvg.org", StringComparison.OrdinalIgnoreCase))
            throw new NotFoundException("That picture's file isn't available.", "art_not_found");
        var (content, type) = await ArtHttp.DownloadAsync(Http, url, MaxDownloadBytes, ct);
        var name = Clean((string?)o["title"] ?? title, 60);
        return new ArtFile(content, type, name.Length > 0 ? name : "SVG", new ArtCreditDto("FreeSVG", null, "CC0", $"{Site}/{Clean((string?)o["slug"])}"));
    }

    /// <summary>The first string field whose name contains one of the hints and reads as a link, made absolute.</summary>
    private static string? FindUrl(JsonObject o, params string[] hints)
    {
        foreach (var hint in hints)
            foreach (var (key, value) in o)
            {
                if (!key.Contains(hint, StringComparison.OrdinalIgnoreCase) || value is not JsonValue v || !v.TryGetValue<string>(out var s)) continue;
                if (string.IsNullOrWhiteSpace(s)) continue;
                if (hint == "svg" && !s.Contains('/') && s.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) s = $"/storage/{s}";
                if (Uri.TryCreate(new Uri(Site), s, out var abs) && abs.Scheme == Uri.UriSchemeHttps) return abs.ToString();
            }
        return null;
    }
}
