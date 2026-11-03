using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
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
/// Apple's Sign in with Apple endpoints as far as our server can tell: a real HTTP server with
/// <c>/auth/keys</c>, <c>/auth/token</c> and <c>/auth/revoke</c>. The token and revoke endpoints refuse
/// a client secret that isn't an ES256 JWT signed with this run's key, for our team, client and Apple's
/// audience, as Apple documents. It mints identity tokens with Apple's issuer. It is not Apple.
/// </summary>
public sealed class AppleIdStub : IAsyncDisposable
{
    public const string ClientId = "blog.invites.app";
    /// <summary>The website's Services ID.</summary>
    public const string WebClientId = "blog.invites.web";
    public const string TeamId = "TEAMSTUB01";
    public const string KeyId = "SIWASTUB01";

    private readonly RSA _signing = RSA.Create(2048);
    private readonly ECDsa _clientKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private WebApplication _app = default!;

    public string BaseUrl { get; private set; } = default!;

    /// <summary>Codes this "Apple" will exchange, each once, and the refresh token each gives.</summary>
    public ConcurrentDictionary<string, string> Codes { get; } = new();
    public ConcurrentBag<string> Revoked { get; } = [];
    /// <summary>Which client a code was issued to, when a test says (Apple refuses it for any other).</summary>
    public ConcurrentDictionary<string, string> CodeClients { get; } = new();
    /// <summary>Each revoked token with the client_id it was revoked as.</summary>
    public ConcurrentBag<(string Token, string ClientId)> RevokedAs { get; } = [];
    public int SecretsRefused;
    /// <summary>Answer the next revoke with 503.</summary>
    public volatile bool FailNextRevoke;

    /// <summary>The .p8 PEM on one line with written-out \n, as it would sit in .env.</summary>
    public string PrivateKeyOneLine => _clientKey.ExportPkcs8PrivateKeyPem().Replace("\n", "\\n");

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.MapGet("/auth/keys", () =>
        {
            var p = _signing.ExportParameters(false);
            return Results.Json(new { keys = new[] { new { kty = "RSA", kid = "stub-apple", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) } } });
        });
        _app.MapPost("/auth/token", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            if (!SecretOk(form)) return Results.Json(new { error = "invalid_client" }, statusCode: 400);
            if (CodeClients.TryGetValue(form["code"].ToString(), out var issuedTo) && issuedTo != form["client_id"])
                return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
            if (form["grant_type"] != "authorization_code" || !Codes.TryRemove(form["code"].ToString(), out var refresh))
                return Results.Json(new { error = "invalid_grant" }, statusCode: 400);
            return Results.Json(new { access_token = "a." + Guid.NewGuid().ToString("N"), token_type = "Bearer", expires_in = 3600, refresh_token = refresh, id_token = "unused" });
        });
        _app.MapPost("/auth/revoke", async (HttpContext ctx) =>
        {
            var form = await ctx.Request.ReadFormAsync();
            if (!SecretOk(form)) return Results.Json(new { error = "invalid_client" }, statusCode: 400);
            if (FailNextRevoke) { FailNextRevoke = false; return Results.StatusCode(503); }
            Revoked.Add(form["token"].ToString());
            RevokedAs.Add((form["token"].ToString(), form["client_id"].ToString()));
            return Results.Ok();
        });
        await _app.StartAsync();
        BaseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    private bool SecretOk(IFormCollection form)
    {
        try
        {
            if (form["client_id"] != ClientId && form["client_id"] != WebClientId) throw new SecurityTokenException("client_id");
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(form["client_secret"].ToString(), new TokenValidationParameters
            {
                ValidIssuer = TeamId,
                ValidAudience = "https://appleid.apple.com",
                IssuerSigningKey = new ECDsaSecurityKey(ECDsa.Create(_clientKey.ExportParameters(false))) { KeyId = KeyId },
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
                ClockSkew = TimeSpan.Zero,
            }, out var token);
            if (((JwtSecurityToken)token).Header.Kid != KeyId) throw new SecurityTokenException("kid");
            if (principal.FindFirst("sub")?.Value != form["client_id"]) throw new SecurityTokenException("sub");
            return true;
        }
        catch (Exception)
        {
            Interlocked.Increment(ref SecretsRefused);
            return false;
        }
    }

    public static string NonceHash(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    /// <summary>An identity token as Apple would give the app; email null as on a later sign-in.</summary>
    public string IdentityToken(string sub, string? email, string rawNonce) =>
        IdentityTokenAsGiven(sub, email, NonceHash(rawNonce), ClientId);

    /// <summary>
    /// An identity token whose nonce is exactly what the requester gave Apple (Apple copies it as-is),
    /// addressed to <paramref name="audience"/>.
    /// </summary>
    public string IdentityTokenAsGiven(string sub, string? email, string nonceAsGiven, string audience)
    {
        var claims = new Dictionary<string, object> { ["sub"] = sub, ["nonce"] = nonceAsGiven, ["nonce_supported"] = true };
        if (email is not null) { claims["email"] = email; claims["email_verified"] = "true"; claims["is_private_email"] = "true"; }
        var now = DateTime.UtcNow;
        var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.WriteToken(handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = "https://appleid.apple.com",
            Audience = audience,
            IssuedAt = now,
            Expires = now.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(_signing) { KeyId = "stub-apple" }, SecurityAlgorithms.RsaSha256),
        }));
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null) await _app.DisposeAsync();
    }
}
