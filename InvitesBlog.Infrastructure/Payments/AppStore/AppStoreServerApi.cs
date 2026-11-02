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

namespace InvitesBlog.Infrastructure.Payments.AppStore;

/// <summary>
/// The App Store Server API over plain HTTP, signed in with an In-App Purchase key (a JWT signed ES256 with
/// the .p8 key: <c>kid</c> the Key ID, <c>iss</c> the Issuer ID, <c>aud</c> "appstoreconnect-v1", <c>bid</c>
/// the bundle id). Configuration is read on each use, so off stays off: <c>AppStore:BundleId</c>,
/// <c>AppStore:IssuerId</c>, <c>AppStore:KeyId</c>, and the key as <c>AppStore:PrivateKey</c> or a file at
/// <c>AppStore:PrivateKeyFile</c>. Everything Apple answers with is a JWS, checked by <see cref="IAppStoreSignedData"/>.
///
/// <para>Production and sandbox are separate servers; which to ask is decided by the caller, from the
/// verified transaction's own environment.</para>
/// </summary>
public sealed class AppStoreServerApi(IHttpClientFactory http, IConfiguration config, IAppStoreSignedData signed, ILogger<AppStoreServerApi> logger)
    : IAppStoreServerApi
{
    public const string ClientName = "app-store";
    // Apple's recommended hosts since 2026-05-05 (App Store Server API changelog); the older
    // api.storekit(-sandbox).itunes.apple.com still work.
    public const string ProductionUrl = "https://api.storekit.apple.com/";
    public const string SandboxUrl = "https://api.storekit-sandbox.apple.com/";
    private static readonly TimeSpan TokenLife = TimeSpan.FromMinutes(20);

    private readonly Lock _tokenLock = new();
    private string? _token;
    private string? _tokenKey;
    private DateTimeOffset _tokenUntil;

    private string? Get(string key) => config[key] is { Length: > 0 } v ? v.Trim() : null;

    private string? KeyPem()
    {
        // An .env file holds one line, so "\n" written out stands for a line break.
        if (Get("AppStore:PrivateKey") is { } pem) return pem.Replace("\\n", "\n");
        if (Get("AppStore:PrivateKeyFile") is { } file && File.Exists(file)
            && File.ReadAllText(file) is { } text && !string.IsNullOrWhiteSpace(text)) return text;
        return null;
    }

    public bool IsConfigured =>
        Get("AppStore:BundleId") is not null && Get("AppStore:IssuerId") is not null && Get("AppStore:KeyId") is not null && KeyPem() is not null;

    private string BaseUrl(bool sandbox) =>
        ((sandbox ? Get("AppStore:SandboxApiBaseUrl") : Get("AppStore:ApiBaseUrl")) ?? (sandbox ? SandboxUrl : ProductionUrl)).TrimEnd('/') + "/";

    private static string E(string s) => Uri.EscapeDataString(s);

    public async Task<AppStoreTransaction?> GetTransactionAsync(string transactionId, bool sandbox, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"inApps/v1/transactions/{E(transactionId)}", null, sandbox, ct);
        if (doc is null) return null;
        var jws = Str(doc.RootElement, "signedTransactionInfo") ?? throw Unreachable();
        return signed.Transaction(jws);
    }

    public async Task<AppStoreSubscriptionStatus?> GetSubscriptionStatusAsync(string originalTransactionId, bool sandbox, CancellationToken ct)
    {
        using var doc = await SendAsync(HttpMethod.Get, $"inApps/v1/subscriptions/{E(originalTransactionId)}", null, sandbox, ct);
        if (doc is null) return null;
        if (!doc.RootElement.TryGetProperty("data", out var groups) || groups.ValueKind != JsonValueKind.Array) return null;
        foreach (var g in groups.EnumerateArray())
        {
            if (!g.TryGetProperty("lastTransactions", out var last) || last.ValueKind != JsonValueKind.Array) continue;
            foreach (var t in last.EnumerateArray())
            {
                // Apple answers for every subscription this customer has with the app; we want this one.
                if (Str(t, "signedTransactionInfo") is not { } info) continue;
                var transaction = signed.Transaction(info);
                if (transaction.OriginalTransactionId != originalTransactionId) continue;
                var renewal = Str(t, "signedRenewalInfo") is { } r ? signed.RenewalInfo(r) : null;
                var status = t.TryGetProperty("status", out var s) && s.TryGetInt32(out var n) ? n : 0;
                return new AppStoreSubscriptionStatus(status, transaction, renewal);
            }
        }
        return null;
    }

    public async Task<IReadOnlyList<string>> NotificationHistoryAsync(DateTimeOffset from, DateTimeOffset to, bool sandbox, CancellationToken ct)
    {
        var found = new List<string>();
        string? page = null;
        var body = JsonSerializer.Serialize(new { startDate = from.ToUnixTimeMilliseconds(), endDate = to.ToUnixTimeMilliseconds() });
        for (var pages = 0; pages < 50; pages++)
        {
            var path = "inApps/v1/notifications/history" + (page is null ? "" : $"?paginationToken={E(page)}");
            using var doc = await SendAsync(HttpMethod.Post, path, body, sandbox, ct);
            if (doc is null) break;
            var j = doc.RootElement;
            if (j.TryGetProperty("notificationHistory", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var n in list.EnumerateArray())
                    if (Str(n, "signedPayload") is { Length: > 0 } p) found.Add(p);
            page = j.TryGetProperty("hasMore", out var more) && more.ValueKind == JsonValueKind.True ? Str(j, "paginationToken") : null;
            if (string.IsNullOrEmpty(page)) break;
        }
        return found;
    }

    // ----- HTTP ----------------------------------------------------------------------------------

    private async Task<JsonDocument?> SendAsync(HttpMethod method, string path, string? body, bool sandbox, CancellationToken ct)
    {
        if (!IsConfigured) throw NotSetUp();
        var first = await TryAsync(method, path, body, sandbox, Token(force: false), ct);
        if (first.StatusCode == HttpStatusCode.Unauthorized)
        {
            // A token from a key since replaced: make a new one, once.
            first.Dispose();
            first = await TryAsync(method, path, body, sandbox, Token(force: true), ct);
        }
        using var response = first;

        if (response.IsSuccessStatusCode)
        {
            try { return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct)); }
            catch (JsonException) { throw Unreachable(); }
        }
        // 400 (an id that isn't one) and 404 (TransactionIdNotFound) are answers: Apple doesn't know it.
        if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.NotFound) return null;
        logger.LogWarning("The App Store Server API answered {Status}.", (int)response.StatusCode);
        throw Unreachable();
    }

    private async Task<HttpResponseMessage> TryAsync(HttpMethod method, string path, string? body, bool sandbox, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, new Uri(new Uri(BaseUrl(sandbox)), path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        try
        {
            return await http.CreateClient(ClientName).SendAsync(request, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning("The App Store Server API didn't answer: {Reason}.", ex.GetType().Name);
            throw Unreachable();
        }
    }

    private string Token(bool force)
    {
        var pem = KeyPem() ?? throw NotSetUp();
        var issuer = Get("AppStore:IssuerId")!;
        var keyId = Get("AppStore:KeyId")!;
        var bundle = Get("AppStore:BundleId")!;
        var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{pem}|{issuer}|{keyId}|{bundle}")));
        lock (_tokenLock)
        {
            if (!force && _token is not null && _tokenKey == fingerprint && DateTimeOffset.UtcNow < _tokenUntil) return _token;
            try
            {
                using var ec = ECDsa.Create();
                ec.ImportFromPem(pem);
                var key = new ECDsaSecurityKey(ECDsa.Create(ec.ExportParameters(true))) { KeyId = keyId };
                var now = DateTime.UtcNow;
                var jwt = new JwtSecurityToken(
                    issuer: issuer, audience: "appstoreconnect-v1",
                    claims: [new Claim("bid", bundle), new Claim("iat", EpochTime.GetIntDate(now).ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64)],
                    notBefore: null, expires: now.Add(TokenLife),
                    signingCredentials: new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256));
                _token = new JwtSecurityTokenHandler().WriteToken(jwt);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            {
                // The type only: a parser's message can quote what it read, and this is a private key.
                logger.LogError("The App Store In-App Purchase key can't be used ({Reason}).", ex.GetType().Name);
                throw NotSetUp();
            }
            _tokenKey = fingerprint;
            _tokenUntil = DateTimeOffset.UtcNow + TokenLife - TimeSpan.FromMinutes(5);
            return _token;
        }
    }

    private static string? Str(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static AppStoreUnavailableException NotSetUp() =>
        new("App Store purchases aren't available right now. Your purchase is safe, and we'll try again later.");

    private static AppStoreUnavailableException Unreachable() =>
        new("We couldn't check this with Apple just now. Your purchase is safe, and we'll try again later.", "app_store_unreachable");
}
