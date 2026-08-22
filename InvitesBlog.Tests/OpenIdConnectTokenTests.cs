using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Verification against a REAL signed token, with the provider's JWKS stubbed out. The shape-level
/// tests next door never reach signature validation, so they can't see what the token turns into
/// afterwards — which is exactly where inbound claim mapping silently renamed `sub` and `email` and
/// made every genuine Google sign-in read as "no email address".
/// </summary>
public class OpenIdConnectTokenTests
{
    private const string ClientId = "client-id.apps.googleusercontent.com";
    private const string Issuer = "https://accounts.google.com";

    private static readonly RSA Key = RSA.Create(2048);
    private const string KeyId = "test-key-1";

    private static GoogleAuthProvider Sut()
    {
        var config = Substitute.For<IConfiguration>();
        config["OAuth:Google:ClientId"].Returns(ClientId);

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient().Returns(_ => new HttpClient(new JwksHandler()));
        return new GoogleAuthProvider(config, factory);
    }

    private static string Sign(Dictionary<string, object> claims, string? audience = ClientId)
    {
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = audience,
            Expires = DateTime.UtcNow.AddMinutes(10),
            Claims = claims,
            SigningCredentials = new SigningCredentials(
                new RsaSecurityKey(Key) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256)
        };
        var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    [Fact]
    public async Task A_valid_google_token_yields_the_subject_email_and_name()
    {
        var token = Sign(new Dictionary<string, object>
        {
            ["sub"] = "108154321234567890123",
            ["email"] = "Zouriel@Example.com",
            ["email_verified"] = "true",
            ["name"] = "Zouriel Corbet"
        });

        var identity = await Sut().VerifyAsync(token);

        Assert.Equal("google", identity.Provider);
        Assert.Equal("108154321234567890123", identity.SubjectId);
        // Lower-cased so the same address can't become two accounts.
        Assert.Equal("zouriel@example.com", identity.Email);
        Assert.Equal("Zouriel Corbet", identity.DisplayName);
    }

    [Fact]
    public async Task A_token_with_no_name_falls_back_to_the_email_local_part()
    {
        var token = Sign(new Dictionary<string, object>
        {
            ["sub"] = "1", ["email"] = "someone@example.com", ["email_verified"] = "true"
        });

        Assert.Equal("someone", (await Sut().VerifyAsync(token)).DisplayName);
    }

    [Fact]
    public async Task An_unverified_email_is_refused()
    {
        var token = Sign(new Dictionary<string, object>
        {
            ["sub"] = "1", ["email"] = "someone@example.com", ["email_verified"] = "false"
        });

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().VerifyAsync(token));
        Assert.Equal("oauth_email_unverified", ex.ErrorCode);
    }

    [Fact]
    public async Task A_token_minted_for_a_different_client_is_refused()
    {
        var token = Sign(new Dictionary<string, object>
        {
            ["sub"] = "1", ["email"] = "someone@example.com", ["email_verified"] = "true"
        }, audience: "someone-elses-app.apps.googleusercontent.com");

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().VerifyAsync(token));
        Assert.Equal("oauth_invalid_token", ex.ErrorCode);
    }

    [Fact]
    public async Task A_token_carrying_no_email_is_reported_as_such()
    {
        var token = Sign(new Dictionary<string, object> { ["sub"] = "1" });

        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().VerifyAsync(token));
        Assert.Equal("oauth_no_email", ex.ErrorCode);
    }

    /// <summary>Serves the public half of <see cref="Key"/> as the provider's JWKS document.</summary>
    private sealed class JwksHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(
                new RsaSecurityKey(Key.ExportParameters(false)) { KeyId = KeyId });
            jwk.Use = "sig";
            jwk.Alg = SecurityAlgorithms.RsaSha256;

            var body = JsonSerializer.Serialize(new { keys = new[] { jwk } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
            });
        }
    }
}
