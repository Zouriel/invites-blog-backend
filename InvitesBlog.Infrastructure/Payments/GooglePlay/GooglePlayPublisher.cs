using System.IdentityModel.Tokens.Jwt;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvitesBlog.Application.Services.Billing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace InvitesBlog.Infrastructure.Payments.GooglePlay;

/// <summary>
/// The Android Publisher API over plain HTTP, signed in with a service account (a signed JWT swapped
/// for an access token at the key's own <c>token_uri</c>). Configuration is read on each use, so
/// off stays off and on needs nothing else: <c>GooglePlay:PackageName</c>, and the key as
/// <c>GooglePlay:ServiceAccountJson</c> or a file at <c>GooglePlay:ServiceAccountFile</c>.
///
/// <para>Never logs a purchase token: they appear only in the request's path, and neither a failure's
/// log line nor its exception carries the URL.</para>
/// </summary>
public sealed class GooglePlayPublisher(IHttpClientFactory http, IConfiguration config, ILogger<GooglePlayPublisher> logger) : IGooglePlayPublisher
{
    public const string ClientName = "google-play";
    public const string DefaultBaseUrl = "https://androidpublisher.googleapis.com/";
    private const string Scope = "https://www.googleapis.com/auth/androidpublisher";
    private static readonly TimeSpan TokenMargin = TimeSpan.FromMinutes(2);

    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private string? _accessTokenKey;
    private DateTimeOffset _accessTokenUntil;

    private string? PackageName => config["GooglePlay:PackageName"] is { Length: > 0 } p ? p : null;
    private string BaseUrl => (config["GooglePlay:ApiBaseUrl"] is { Length: > 0 } b ? b : DefaultBaseUrl).TrimEnd('/') + "/";

    private string? KeyJson()
    {
        if (config["GooglePlay:ServiceAccountJson"] is { Length: > 0 } json) return json;
        if (config["GooglePlay:ServiceAccountFile"] is { Length: > 0 } file && File.Exists(file)
            && File.ReadAllText(file) is { } text && !string.IsNullOrWhiteSpace(text)) return text;
        return null;
    }

    public bool IsConfigured => PackageName is not null && KeyJson() is not null;

    private string App => $"androidpublisher/v3/applications/{Uri.EscapeDataString(PackageName ?? "")}";
    private static string E(string s) => Uri.EscapeDataString(s);

    public async Task<PlayProductPurchase?> GetProductAsync(string productId, string token, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{App}/purchases/products/{E(productId)}/tokens/{E(token)}", ct);
        if (doc is null) return null;
        var j = doc.RootElement;
        return new PlayProductPurchase(
            Int(j, "purchaseState") ?? 0, Int(j, "consumptionState") ?? 0, Int(j, "acknowledgementState") ?? 0, Int(j, "purchaseType"),
            Str(j, "orderId"), Str(j, "productId"), Str(j, "obfuscatedExternalAccountId"), Str(j, "obfuscatedExternalProfileId"));
    }

    public async Task<PlaySubscriptionPurchase?> GetSubscriptionAsync(string token, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{App}/purchases/subscriptionsv2/tokens/{E(token)}", ct);
        if (doc is null) return null;
        var j = doc.RootElement;
        var lines = new List<PlaySubscriptionLine>();
        if (j.TryGetProperty("lineItems", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var l in items.EnumerateArray())
            {
                DateTimeOffset? expiry = DateTimeOffset.TryParse(Str(l, "expiryTime"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
                    ? Micro(when) : null;
                var autoRenew = l.TryGetProperty("autoRenewingPlan", out var plan) && plan.ValueKind == JsonValueKind.Object
                    && plan.TryGetProperty("autoRenewEnabled", out var on) && on.ValueKind == JsonValueKind.True;
                lines.Add(new PlaySubscriptionLine(Str(l, "productId") ?? "", expiry, Str(l, "latestSuccessfulOrderId"), autoRenew));
            }
        var account = j.TryGetProperty("externalAccountIdentifiers", out var ids) && ids.ValueKind == JsonValueKind.Object
            ? Str(ids, "obfuscatedExternalAccountId") : null;
        return new PlaySubscriptionPurchase(
            Str(j, "subscriptionState") ?? "SUBSCRIPTION_STATE_UNSPECIFIED",
            Str(j, "acknowledgementState") == "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED",
            j.TryGetProperty("testPurchase", out var test) && test.ValueKind == JsonValueKind.Object,
            Str(j, "linkedPurchaseToken"), account, lines);
    }

    public Task<bool> ConsumeAsync(string productId, string token, CancellationToken ct) =>
        PostAsync($"{App}/purchases/products/{E(productId)}/tokens/{E(token)}:consume", null, ct);

    public Task<bool> AcknowledgeSubscriptionAsync(string productId, string token, CancellationToken ct) =>
        PostAsync($"{App}/purchases/subscriptions/{E(productId)}/tokens/{E(token)}:acknowledge", "{}", ct);

    public Task<bool> CancelSubscriptionAsync(string token, CancellationToken ct) =>
        PostAsync($"{App}/purchases/subscriptionsv2/tokens/{E(token)}:cancel",
            """{"cancellationContext":{"cancellationType":"DEVELOPER_REQUESTED_STOP_PAYMENTS"}}""", ct);

    public async Task<PlayOrderTotal?> GetOrderTotalAsync(string orderId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"{App}/orders/{E(orderId)}", ct, forbiddenIsMissing: true);
        if (doc is null || !doc.RootElement.TryGetProperty("total", out var total) || total.ValueKind != JsonValueKind.Object) return null;
        var currency = Str(total, "currencyCode");
        if (string.IsNullOrWhiteSpace(currency)) return null;
        var units = decimal.TryParse(Str(total, "units"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var u) ? u : 0m;
        var nanos = Int(total, "nanos") ?? 0;
        return new PlayOrderTotal(Math.Round(units + nanos / 1_000_000_000m, 2), currency);
    }

    public async Task<IReadOnlyList<PlayVoidedPurchase>> ListVoidedAsync(CancellationToken ct)
    {
        var found = new List<PlayVoidedPurchase>();
        string? page = null;
        do
        {
            var path = $"{App}/purchases/voidedpurchases?type=1" + (page is null ? "" : $"&pageSelection.token={E(page)}");
            using var doc = await GetJsonAsync(path, ct);
            if (doc is null) break;
            var j = doc.RootElement;
            if (j.TryGetProperty("voidedPurchases", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var v in list.EnumerateArray())
                    if (Str(v, "purchaseToken") is { } token) found.Add(new PlayVoidedPurchase(token, Str(v, "orderId")));
            page = j.TryGetProperty("tokenPagination", out var tp) && tp.ValueKind == JsonValueKind.Object ? Str(tp, "nextPageToken") : null;
        } while (!string.IsNullOrEmpty(page));
        return found;
    }

    private static DateTimeOffset Micro(DateTimeOffset t) => new(t.UtcTicks - t.UtcTicks % 10, TimeSpan.Zero);

    private static string? Str(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static int? Int(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private async Task<JsonDocument?> GetJsonAsync(string path, CancellationToken ct, bool forbiddenIsMissing = false)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, ct);
        if (response.IsSuccessStatusCode)
        {
            try { return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); }
            catch (JsonException) { throw Unreachable(); }
        }
        if (forbiddenIsMissing && response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized) return null;
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Gone) return null;
        throw Failed(response.StatusCode);
    }

    private async Task<bool> PostAsync(string path, string? body, CancellationToken ct)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body, ct);
        if (response.IsSuccessStatusCode) return true;
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound or HttpStatusCode.Gone or HttpStatusCode.Conflict) return false;
        throw Failed(response.StatusCode);
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string? body, CancellationToken ct)
    {
        if (!IsConfigured) throw NotSetUp();
        var token = await AccessTokenAsync(force: false, ct);
        var response = await TryAsync(method, path, body, token, ct);
        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            response.Dispose();
            token = await AccessTokenAsync(force: true, ct);
            response = await TryAsync(method, path, body, token, ct);
        }
        return response;
    }

    private async Task<HttpResponseMessage> TryAsync(HttpMethod method, string path, string? body, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(BaseUrl), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        else if (method == HttpMethod.Post) request.Content = new ByteArrayContent([]);
        try
        {
            return await http.CreateClient(ClientName).SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Google Play didn't answer: {Reason}.", ex.GetType().Name);
            throw Unreachable();
        }
    }

    private async Task<string> AccessTokenAsync(bool force, CancellationToken ct)
    {
        var keyJson = KeyJson() ?? throw NotSetUp();
        var keyId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(keyJson)));
        await _tokenLock.WaitAsync(ct);
        try
        {
            if (!force && _accessToken is not null && _accessTokenKey == keyId && DateTimeOffset.UtcNow < _accessTokenUntil)
                return _accessToken;

            string email, pem, tokenUri;
            string? privateKeyId;
            try
            {
                using var key = JsonDocument.Parse(keyJson);
                var j = key.RootElement;
                email = Str(j, "client_email") ?? throw new FormatException();
                pem = Str(j, "private_key") ?? throw new FormatException();
                tokenUri = Str(j, "token_uri") ?? "https://oauth2.googleapis.com/token";
                privateKeyId = Str(j, "private_key_id");
            }
            catch (Exception ex) when (ex is JsonException or FormatException)
            {
                // The type only: a parser's message can quote what it read, and this is a private key.
                logger.LogError("GooglePlay:ServiceAccountJson can't be read ({Reason}).", ex.GetType().Name);
                throw NotSetUp();
            }

            string assertion;
            try { assertion = Sign(email, pem, tokenUri, privateKeyId); }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                logger.LogError("The Google Play service account's private key can't be used ({Reason}).", ex.GetType().Name);
                throw NotSetUp();
            }

            using var response = await RequestTokenAsync(tokenUri, assertion, ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogError("Google refused the service account's sign-in: {Status}.", (int)response.StatusCode);
                throw Failed(response.StatusCode);
            }
            JsonDocument doc;
            try { doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); }
            catch (JsonException) { throw Unreachable(); }
            using var _ = doc;
            _accessToken = Str(doc.RootElement, "access_token") ?? throw Unreachable();
            var lifetime = TimeSpan.FromSeconds(Int(doc.RootElement, "expires_in") ?? 3600);
            _accessTokenUntil = DateTimeOffset.UtcNow + lifetime - TokenMargin;
            _accessTokenKey = keyId;
            return _accessToken;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private async Task<HttpResponseMessage> RequestTokenAsync(string tokenUri, string assertion, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenUri)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = assertion,
            }),
        };
        try { return await http.CreateClient(ClientName).SendAsync(request, ct); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("Google's sign-in didn't answer: {Reason}.", ex.GetType().Name);
            throw Unreachable();
        }
    }

    private static string Sign(string email, string pem, string audience, string? keyId)
    {
        using var rsa = RSA.Create();
        rsa.ImportFromPem(pem);
        // Exported parameters, not the RSA object: the handler can cache the key past this using block.
        var key = new RsaSecurityKey(rsa.ExportParameters(true)) { KeyId = keyId };
        var now = DateTime.UtcNow;
        var jwt = new JwtSecurityToken(
            issuer: email, audience: audience,
            claims: [new Claim("scope", Scope)],
            notBefore: now, expires: now.AddMinutes(30),
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private GooglePlayUnavailableException Failed(HttpStatusCode status)
    {
        logger.LogWarning("Google Play answered {Status}.", (int)status);
        // Auth and permission failures are ours to fix, and a retry later will work once they are:
        // neither says anything about the purchase, so nothing may be concluded from them.
        return Unreachable();
    }

    private static GooglePlayUnavailableException NotSetUp() =>
        new("Google Play purchases aren't available right now. Your purchase is safe, and we'll try again later.");

    private static GooglePlayUnavailableException Unreachable() =>
        new("We couldn't check this with Google Play just now. Your purchase is safe, and we'll try again later.", "google_play_unreachable");
}

/// <summary>
/// Verifies the Google-signed token Pub/Sub puts on a push (<c>Authorization: Bearer</c>): issuer, signature, that it
/// is addressed to us (<c>GooglePlay:Notifications:Audience</c>), and that it comes from our own Pub/Sub
/// service account (<c>GooglePlay:Notifications:ServiceAccountEmail</c>, with a verified email).
/// Anyone with a Google account can get a Google-signed token: the audience and the email are what make it ours.
/// </summary>
public sealed class GooglePubSubTokenVerifier(IConfiguration config, IHttpClientFactory http)
    : Security.OpenIdConnectAuthProvider(config["GooglePlay:Notifications:Audience"], http)
{
    private readonly string? _sender = config["GooglePlay:Notifications:ServiceAccountEmail"]?.Trim().ToLowerInvariant();
    private readonly string _jwks = config["GooglePlay:Notifications:JwksUrl"] is { Length: > 0 } u ? u : "https://www.googleapis.com/oauth2/v3/certs";

    public override string Provider => "google-pubsub";
    protected override string ConfigSection => "GooglePubSub";
    protected override string JwksUri => _jwks;
    protected override string[] ValidIssuers => ["https://accounts.google.com", "accounts.google.com"];
    protected override string AuthorizeUrl => "";

    private readonly IConfiguration _config = config;

    public bool Ready => IsConfigured && !string.IsNullOrWhiteSpace(_sender)
                         && !string.IsNullOrWhiteSpace(_config["GooglePlay:Notifications:Audience"]);

    /// <summary>Throws (401) unless this push really is from our subscription.</summary>
    public async Task VerifyPushAsync(string? authorization, CancellationToken ct)
    {
        const string prefix = "Bearer ";
        if (authorization is null || !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new Application.Exceptions.UnauthorizedException("No token.", "pubsub_no_token");
        // VerifyAsync requires the email, checks email_verified, and answers 401 for a bad signature,
        // issuer, audience or expiry.
        var identity = await VerifyAsync(authorization[prefix.Length..].Trim(), ct);
        if (!string.Equals(identity.Email, _sender, StringComparison.Ordinal))
            throw new Application.Exceptions.UnauthorizedException("Not our subscription.", "pubsub_wrong_sender");
    }
}
