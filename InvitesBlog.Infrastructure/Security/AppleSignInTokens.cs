using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Common;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace InvitesBlog.Infrastructure.Security;

/// <summary>
/// Apple's <c>/auth/token</c> and <c>/auth/revoke</c> for Sign in with Apple, authenticated with a
/// client secret: a short ES256 JWT signed with the Sign in with Apple key (iss = team id, sub = the
/// bundle id, aud = https://appleid.apple.com).
/// <para>
/// Settings: <c>OAuth:Apple:ClientId</c> (blog.invites.app), <c>OAuth:Apple:TeamId</c>,
/// <c>OAuth:Apple:KeyId</c> and <c>OAuth:Apple:PrivateKey</c> (the .p8 PEM; written-out \n is fine).
/// Missing any of them, or a key that won't load, leaves this off. Neither the key, a code nor a token
/// is ever logged: only Apple's status and its short error word.
/// </para>
/// </summary>
public sealed class AppleSignInTokens : IAppleSignInTokens, IDisposable
{
    public const string ClientName = "apple-signin";
    private static readonly TimeSpan SecretLifetime = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory http;
    private readonly ILogger<AppleSignInTokens> logger;
    private readonly string baseUrl;
    private readonly string? clientId;
    /// <summary>Every client of ours a code or token may belong to: the app, and the website's Services ID.</summary>
    private readonly HashSet<string> ownClients = new(StringComparer.Ordinal);
    private readonly string? teamId;
    private readonly string? keyId;
    private readonly ECDsa? key;

    public AppleSignInTokens(IConfiguration config, IHttpClientFactory http, ILogger<AppleSignInTokens> logger)
    {
        this.http = http;
        this.logger = logger;
        baseUrl = AppleAuthProvider.AppleBaseUrl(config);
        clientId = config["OAuth:Apple:ClientId"]?.Trim();
        teamId = config["OAuth:Apple:TeamId"]?.Trim();
        keyId = config["OAuth:Apple:KeyId"]?.Trim();
        var pem = config["OAuth:Apple:PrivateKey"];
        if (!string.IsNullOrWhiteSpace(clientId)) ownClients.Add(clientId);
        // The website's client, whether or not web sign-in is on right now: a token it issued while it was
        // on must still be revocable after it is switched off.
        if (AppleWebSettings.From(config).ClientId is { } webClient) ownClients.Add(webClient);

        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(teamId) ||
            string.IsNullOrWhiteSpace(keyId) || string.IsNullOrWhiteSpace(pem))
            return;

        var loaded = ECDsa.Create();
        try
        {
            loaded.ImportPkcs8PrivateKey(Convert.FromBase64String(Notifications.Push.ApnsClient.KeyBody(pem)), out _);
            key = loaded;
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException or ArgumentException)
        {
            logger.LogError("The Sign in with Apple key in OAuth:Apple:PrivateKey couldn't be read ({Error}); Apple token revocation is off.",
                ex.GetType().Name);
            loaded.Dispose();
        }
    }

    public bool IsConfigured => key is not null;

    public async Task<string?> ExchangeAsync(string authorizationCode, string? client = null, CancellationToken ct = default)
    {
        if (!IsConfigured || string.IsNullOrWhiteSpace(authorizationCode)) return null;
        client ??= clientId!;
        if (!ownClients.Contains(client))
        {
            logger.LogWarning("Not exchanging a Sign in with Apple code for a client that isn't ours.");
            return null;
        }
        try
        {
            using var response = await PostAsync("/auth/token", new Dictionary<string, string>
            {
                ["client_id"] = client,
                ["client_secret"] = ClientSecret(client),
                ["code"] = authorizationCode,
                ["grant_type"] = "authorization_code",
            }, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Apple refused the sign-in code exchange: {Status} {Error}", (int)response.StatusCode, ErrorWord(body));
                return null;
            }
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("refresh_token", out var rt) && rt.GetString() is { Length: > 0 } token)
                return token;
            logger.LogWarning("Apple's code exchange answered without a refresh token.");
            return null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning("Couldn't exchange the Sign in with Apple code ({Error}).", ex.GetType().Name);
            return null;
        }
    }

    public async Task RevokeAsync(string refreshToken, string? client = null, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new InvalidOperationException("Sign in with Apple has no key (OAuth:Apple:KeyId/PrivateKey), so its token can't be revoked yet.");
        client ??= clientId!;
        if (!ownClients.Contains(client))
            throw new InvalidOperationException("The Apple token belongs to a client this server isn't configured for (OAuth:Apple:WebClientId?).");
        using var response = await PostAsync("/auth/revoke", new Dictionary<string, string>
        {
            ["client_id"] = client,
            ["client_secret"] = ClientSecret(client),
            ["token"] = refreshToken,
            ["token_type_hint"] = "refresh_token",
        }, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Apple didn't revoke the token: {(int)response.StatusCode} {ErrorWord(body)}");
        }
    }

    private async Task<HttpResponseMessage> PostAsync(string path, Dictionary<string, string> form, CancellationToken ct)
    {
        var client = http.CreateClient(ClientName);
        using var content = new FormUrlEncodedContent(form);
        return await client.PostAsync(baseUrl + path, content, ct);
    }

    /// <summary>The client secret Apple wants instead of a password, for one of our clients (its <c>sub</c>);
    /// made fresh for each call. One Sign in with Apple key serves the app and the website alike.</summary>
    internal string ClientSecret(string? client = null)
    {
        var now = DateTime.UtcNow;
        var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = teamId,
            Audience = AppleAuthProvider.Issuer,
            Subject = new ClaimsIdentity([new Claim("sub", client ?? clientId!)]),
            IssuedAt = now,
            NotBefore = null,
            Expires = now + SecretLifetime,
            SigningCredentials = new SigningCredentials(new ECDsaSecurityKey(key!) { KeyId = keyId }, SecurityAlgorithms.EcdsaSha256),
        };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    /// <summary>Apple's <c>error</c> value (e.g. invalid_grant), shortened; never the body itself.</summary>
    private static string ErrorWord(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.GetString() is { } word)
                return new string(word.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(40).ToArray());
        }
        catch (JsonException) { }
        return "(no error word)";
    }

    public void Dispose() => key?.Dispose();
}
