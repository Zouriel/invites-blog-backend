using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace InvitesBlog.Tests.Integration;

/// <summary>
/// Google, as far as our client can tell: a real HTTP server answering the Android Publisher paths and
/// the service-account token exchange, which refuses a badly signed assertion or a missing access token.
/// The real client talks to it over a socket, so the URLs and the sign-in are exercised, not assumed.
/// Its paths come from Google's published REST reference; it is not Google.
/// </summary>
public sealed class GooglePlayStub : IAsyncDisposable
{
    public const string Package = "blog.invites.app";
    public const string Audience = "https://api.test/api/billing/google-play/notifications";
    public const string PubSubEmail = "pubsub-push@test-project.iam.gserviceaccount.com";

    private readonly RSA _key = RSA.Create(2048);
    private WebApplication _app = default!;
    public string BaseUrl { get; private set; } = default!;

    public ConcurrentDictionary<string, object> Products { get; } = new();
    public ConcurrentDictionary<string, object> Subscriptions { get; } = new();
    public ConcurrentDictionary<string, object> Orders { get; } = new();
    public ConcurrentBag<object> Voided { get; } = [];
    public ConcurrentQueue<(string Method, string Path)> Requests { get; } = new();
    /// <summary>Answer the next request whose path contains the key with this status, instead of normally.</summary>
    public ConcurrentDictionary<string, int> FailNext { get; } = new();
    public int TokensIssued;
    public int AssertionsRefused;
    /// <summary>Slow Google's reads down so racing callers really do overlap.</summary>
    public int ReadDelayMs;

    public int Count(string fragment) => Requests.Count(r => r.Path.Contains(fragment, StringComparison.Ordinal));

    public string ServiceAccountJson() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["type"] = "service_account",
        ["client_email"] = "publisher@test-project.iam.gserviceaccount.com",
        ["private_key_id"] = "stub-key",
        ["private_key"] = _key.ExportPkcs8PrivateKeyPem(),
        ["token_uri"] = $"{BaseUrl}/token",
    });

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapPost("/token", (Delegate)Token);
        _app.MapGet("/jwks", () => Results.Json(new { keys = new[] { Jwk() } }));
        _app.Map("/androidpublisher/v3/{**rest}", (Delegate)Publisher);
        await _app.StartAsync();
        BaseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    private object Jwk()
    {
        var p = _key.ExportParameters(false);
        return new { kty = "RSA", use = "sig", alg = "RS256", kid = "stub-key", n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) };
    }

    private async Task<IResult> Token(HttpContext ctx)
    {
        var form = await ctx.Request.ReadFormAsync();
        try
        {
            if (form["grant_type"] != "urn:ietf:params:oauth:grant-type:jwt-bearer") throw new SecurityTokenException("grant");
            var claims = new JwtSecurityTokenHandler { MapInboundClaims = false }.ValidateToken(form["assertion"], new TokenValidationParameters
            {
                ValidIssuer = "publisher@test-project.iam.gserviceaccount.com",
                ValidAudience = $"{BaseUrl}/token",
                IssuerSigningKey = new RsaSecurityKey(_key.ExportParameters(false)),
            }, out _);
            // As Google: "Invalid JWT: iat (issued at) is not set."
            if (claims.FindFirst("iat") is null) throw new SecurityTokenException("iat");
            if (claims.FindFirst("scope")?.Value != "https://www.googleapis.com/auth/androidpublisher") throw new SecurityTokenException("scope");
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            Interlocked.Increment(ref AssertionsRefused);
            return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
        }
        Interlocked.Increment(ref TokensIssued);
        return Results.Json(new { access_token = "stub-access", token_type = "Bearer", expires_in = 3600 });
    }

    private async Task<IResult> Publisher(HttpContext ctx)
    {
        // The raw path, not the route's decoded one: this is about what really went over the wire.
        var raw = (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget ?? "").Split('?')[0];
        var rest = raw["/androidpublisher/v3/".Length..];
        Requests.Enqueue((ctx.Request.Method, raw));
        if (ctx.Request.Headers.Authorization != "Bearer stub-access") return Results.Json(new { error = "unauthenticated" }, statusCode: 401);
        foreach (var (key, status) in FailNext)
            if (raw.Contains(key, StringComparison.Ordinal) && FailNext.TryRemove(key, out _))
                return Results.Json(new { error = "forced" }, statusCode: status);

        var prefix = $"applications/{Package}/";
        if (!rest.StartsWith(prefix, StringComparison.Ordinal)) return Results.Json(new { error = "no such package" }, statusCode: 404);
        var path = rest[prefix.Length..];
        var parts = path.Split('/');

        if (ctx.Request.Method == "GET" && ReadDelayMs > 0) await Task.Delay(ReadDelayMs);

        // purchases/products/{p}/tokens/{t}[:consume]
        if (parts is ["purchases", "products", var product, "tokens", var ptoken])
        {
            var consume = ptoken.EndsWith(":consume", StringComparison.Ordinal);
            var t = consume ? ptoken[..^":consume".Length] : ptoken;
            if (!Products.TryGetValue($"{product}/{t}", out var body)) return Results.Json(new { error = "not found" }, statusCode: 404);
            if (consume)
            {
                var d = (Dictionary<string, object?>)body;
                if (Equals(d["consumptionState"], 1)) return Results.Json(new { error = "already consumed" }, statusCode: 400);
                d["consumptionState"] = 1;
                d["acknowledgementState"] = 1;
                return Results.Ok();
            }
            return Results.Json(body);
        }
        if (parts is ["purchases", "subscriptionsv2", "tokens", var stoken])
        {
            var cancel = stoken.EndsWith(":cancel", StringComparison.Ordinal);
            var t = cancel ? stoken[..^":cancel".Length] : stoken;
            if (!Subscriptions.TryGetValue(t, out var body)) return Results.Json(new { error = "not found" }, statusCode: 404);
            if (cancel)
            {
                using var doc = await JsonDocument.ParseAsync(ctx.Request.Body);
                if (doc.RootElement.GetProperty("cancellationContext").GetProperty("cancellationType").GetString() is not ("DEVELOPER_REQUESTED_STOP_PAYMENTS" or "USER_REQUESTED_STOP_RENEWALS"))
                    return Results.Json(new { error = "bad type" }, statusCode: 400);
                ((Dictionary<string, object?>)body)["subscriptionState"] = "SUBSCRIPTION_STATE_CANCELED";
                return Results.Ok();
            }
            return Results.Json(body);
        }
        if (parts is ["purchases", "subscriptions", _, "tokens", var atoken] && atoken.EndsWith(":acknowledge", StringComparison.Ordinal))
        {
            var t = atoken[..^":acknowledge".Length];
            if (!Subscriptions.TryGetValue(t, out var body)) return Results.Json(new { error = "not found" }, statusCode: 404);
            ((Dictionary<string, object?>)body)["acknowledgementState"] = "ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED";
            return Results.Ok();
        }
        if (parts is ["orders", var id])
            return Orders.TryGetValue(id, out var order) ? Results.Json(order) : Results.Json(new { error = "not found" }, statusCode: 404);
        if (parts is ["purchases", "voidedpurchases"])
            return Results.Json(new { voidedPurchases = Voided.ToArray() });
        return Results.Json(new { error = "no such method", path }, statusCode: 404);
    }

    /// <summary>What Pub/Sub would sign and send.</summary>
    public string PubSubToken(string? audience = null, string? email = null, bool verified = true, RSA? signWith = null)
    {
        var claims = new List<Claim> { new("sub", "1234"), new("email", email ?? PubSubEmail), new("email_verified", verified ? "true" : "false", ClaimValueTypes.Boolean) };
        var key = new RsaSecurityKey((signWith ?? _key).ExportParameters(true)) { KeyId = "stub-key" };
        var jwt = new JwtSecurityToken("https://accounts.google.com", audience ?? Audience, claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(30),
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _key.Dispose();
    }
}
