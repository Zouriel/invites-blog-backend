using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Notifications.Push;

/// <summary>What Apple said about one message.</summary>
public enum ApnsOutcome
{
    Sent,
    /// <summary>The token is dead (uninstalled, or never valid for this app): forget the device.</summary>
    Gone,
    Failed,
    /// <summary>No key for that environment, so nothing was sent.</summary>
    NotConfigured,
}

/// <summary>
/// The Apple push settings, from <c>Push:Apns:*</c>. Off unless the key id, team id and key are all set.
/// The key is the .p8 file's contents (one line with \n for newlines is fine); it is never logged.
/// </summary>
public sealed record ApnsSettings(
    string? KeyId, string? TeamId, string? PrivateKey, string? SandboxKeyId, string? SandboxPrivateKey, string Topic)
{
    public const string DefaultTopic = "blog.invites.app";

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(TeamId) && !string.IsNullOrWhiteSpace(PrivateKey);

    public bool HasSandboxKey => !string.IsNullOrWhiteSpace(SandboxKeyId) && !string.IsNullOrWhiteSpace(SandboxPrivateKey);

    public static ApnsSettings From(IConfiguration config) => new(
        config["Push:Apns:KeyId"], config["Push:Apns:TeamId"], config["Push:Apns:PrivateKey"],
        config["Push:Apns:SandboxKeyId"], config["Push:Apns:SandboxPrivateKey"],
        string.IsNullOrWhiteSpace(config["Push:Apns:Topic"]) ? DefaultTopic : config["Push:Apns:Topic"]!.Trim());
}

/// <summary>
/// Talks to APNs with token-based auth: an ES256 JWT made from the .p8 key and reused for 50 minutes
/// (Apple refuses a new one more often than every 20 minutes and any older than an hour), sent over
/// HTTP/2 to api.push.apple.com, or api.sandbox.push.apple.com for a token from a build run in Xcode.
/// The device token is in the request's path, so the HTTP client has no request logging, and failures
/// are logged with the status and Apple's one-word reason only.
/// </summary>
public sealed class ApnsClient : IDisposable
{
    public const string ClientName = "apns";
    public const string ProductionHost = "api.push.apple.com";
    public const string SandboxHost = "api.sandbox.push.apple.com";
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan JwtLifetime = TimeSpan.FromMinutes(50);

    private readonly IHttpClientFactory _http;
    private readonly TimeProvider _clock;
    private readonly ILogger<ApnsClient> _logger;
    private readonly Signer? _production;
    private readonly Signer? _sandbox;
    private readonly string _topic;

    public ApnsClient(IHttpClientFactory http, ApnsSettings settings, TimeProvider clock, ILogger<ApnsClient> logger)
    {
        _http = http;
        _clock = clock;
        _logger = logger;
        _topic = settings.Topic;
        if (!settings.IsConfigured) return;
        _production = Load(settings.KeyId!, settings.TeamId!, settings.PrivateKey!, "PrivateKey");
        _sandbox = settings.HasSandboxKey
            ? Load(settings.SandboxKeyId!, settings.TeamId!, settings.SandboxPrivateKey!, "SandboxPrivateKey")
            : _production;
    }

    public bool IsConfigured => _production is not null;

    private Signer? Load(string keyId, string teamId, string pem, string setting)
    {
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(KeyBody(pem)), out _);
            return new Signer(key, keyId.Trim(), teamId.Trim(), _clock);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            // The kind of failure only, never the key.
            _logger.LogError("APNs key in Push:Apns:{Setting} couldn't be read ({Error}); Apple push is off for it.",
                setting, ex.GetType().Name);
            key.Dispose();
            return null;
        }
    }

    /// <summary>The .p8 PEM's base64 body: header, footer, whitespace and written-out \n removed.</summary>
    internal static string KeyBody(string pem) =>
        new(pem.Replace("\\n", "\n")
            .Replace("-----BEGIN PRIVATE KEY-----", "").Replace("-----END PRIVATE KEY-----", "")
            .Where(c => !char.IsWhiteSpace(c)).ToArray());

    /// <summary>The JSON Apple wants: the alert under "aps", and each data entry at the top level.</summary>
    public static string Payload(string title, string body, IReadOnlyDictionary<string, string>? data)
    {
        var root = new Dictionary<string, object>
        {
            ["aps"] = new Dictionary<string, object>
            {
                ["alert"] = new Dictionary<string, string> { ["title"] = title, ["body"] = body },
                ["sound"] = "default",
            },
        };
        if (data is not null)
            foreach (var (k, v) in data)
                if (k != "aps") root[k] = v;
        return JsonSerializer.Serialize(root);
    }

    public async Task<ApnsOutcome> SendAsync(string deviceToken, bool sandbox, string payloadJson, CancellationToken ct)
    {
        var signer = sandbox ? _sandbox : _production;
        if (signer is null) return ApnsOutcome.NotConfigured;
        var host = sandbox ? SandboxHost : ProductionHost;

        using var request = new HttpRequestMessage(HttpMethod.Post, $"https://{host}/3/device/{Uri.EscapeDataString(deviceToken)}")
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionOrHigher,
            Content = new StringContent(payloadJson, Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("bearer", signer.Jwt());
        request.Headers.TryAddWithoutValidation("apns-topic", _topic);
        request.Headers.TryAddWithoutValidation("apns-push-type", "alert");
        request.Headers.TryAddWithoutValidation("apns-priority", "10");
        // A day: an invitation or a reply is still worth hearing about after a phone was off overnight.
        request.Headers.TryAddWithoutValidation("apns-expiration",
            _clock.GetUtcNow().AddDays(1).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture));

        try
        {
            using var response = await _http.CreateClient(ClientName).SendAsync(request, ct);
            if (response.IsSuccessStatusCode) return ApnsOutcome.Sent;
            var reason = await ReasonAsync(response, ct);
            if (response.StatusCode == HttpStatusCode.Gone
                || reason is "BadDeviceToken" or "Unregistered" or "DeviceTokenNotForTopic")
                return ApnsOutcome.Gone;
            // A refused provider token is made again for the next send.
            if (reason is "ExpiredProviderToken" or "InvalidProviderToken") signer.Reset();
            _logger.LogWarning("APNs refused a message: HTTP {Status} {Reason}.", (int)response.StatusCode, reason ?? "-");
            return ApnsOutcome.Failed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            // The type only: an exception's message can carry the request URL, and with it the token.
            _logger.LogWarning("Couldn't reach APNs: {Error}.", ex.GetType().Name);
            return ApnsOutcome.Failed;
        }
    }

    /// <summary>Apple's "reason" word from an error body. Only plain letters are kept, so nothing else reaches a log.</summary>
    private static async Task<string?> ReasonAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(body)) return null;
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("reason", out var r) && r.ValueKind == JsonValueKind.String
                   && r.GetString() is { Length: > 0 and <= 60 } s && s.All(char.IsAsciiLetter)
                ? s : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed class Signer(ECDsa key, string keyId, string teamId, TimeProvider clock) : IDisposable
    {
        private readonly Lock _lock = new();
        private (string Jwt, DateTimeOffset IssuedAt)? _jwt;

        public string Jwt()
        {
            var now = clock.GetUtcNow();
            lock (_lock)
            {
                if (_jwt is { } cached && now - cached.IssuedAt < JwtLifetime && now >= cached.IssuedAt) return cached.Jwt;
                var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = keyId }));
                var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new { iss = teamId, iat = now.ToUnixTimeSeconds() }));
                var input = $"{header}.{claims}";
                // JWS ES256 is r||s (IEEE P1363), which is SignData's default format.
                var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256);
                var jwt = $"{input}.{B64(signature)}";
                _jwt = (jwt, now);
                return jwt;
            }
        }

        public void Reset()
        {
            lock (_lock) _jwt = null;
        }

        public void Dispose() => key.Dispose();
    }

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    public void Dispose()
    {
        if (!ReferenceEquals(_sandbox, _production)) _sandbox?.Dispose();
        _production?.Dispose();
    }
}
