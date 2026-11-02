using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Sign in with Apple's identity token, verified against a stand-in for Apple's JWKS. Each refusal
/// test changes exactly one thing from a token that is otherwise accepted (the first test).
/// </summary>
public class AppleAuthProviderTests
{
    private const string BundleId = "blog.invites.app";
    private const string RawNonce = "6f1c0a8e-raw-nonce-the-app-kept";
    private const string KeyId = "apple-test-key";

    private static readonly RSA AppleKey = RSA.Create(2048);
    private static readonly RSA SomeoneElsesKey = RSA.Create(2048);

    private static string Hash(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static AppleAuthProvider Sut(string? clientId = BundleId)
    {
        var config = Substitute.For<IConfiguration>();
        config["OAuth:Apple:ClientId"].Returns(clientId);
        config["OAuth:Apple:BaseUrl"].Returns("https://apple.test");
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient().Returns(_ => new HttpClient(new JwksHandler()));
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new JwksHandler()));
        return new AppleAuthProvider(config, factory);
    }

    private static string Token(
        string audience = BundleId,
        string issuer = "https://appleid.apple.com",
        string? nonce = null,
        DateTime? expires = null,
        RSA? key = null,
        string? email = "abc123xyz@privaterelay.appleid.com",
        object? emailVerified = null,
        bool omitNonce = false)
    {
        var claims = new Dictionary<string, object> { ["sub"] = "001234.abcdef0123456789.0123" };
        if (email is not null) claims["email"] = email;
        if (email is not null) claims["email_verified"] = emailVerified ?? true;
        if (!omitNonce) claims["nonce"] = nonce ?? Hash(RawNonce);
        var exp = expires ?? DateTime.UtcNow.AddMinutes(10);
        var descriptor = new SecurityTokenDescriptor
        {
            Issuer = issuer,
            Audience = audience,
            IssuedAt = exp.AddMinutes(-20),
            NotBefore = exp.AddMinutes(-20),
            Expires = exp,
            Claims = claims,
            SigningCredentials = new SigningCredentials(new RsaSecurityKey(key ?? AppleKey) { KeyId = KeyId }, SecurityAlgorithms.RsaSha256),
        };
        var handler = new JwtSecurityTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.WriteToken(handler.CreateToken(descriptor));
    }

    [Fact]
    public async Task A_valid_token_with_the_right_nonce_yields_apple_s_subject_and_email()
    {
        var identity = await Sut().VerifyAsync(Token(), RawNonce);

        Assert.Equal("apple", identity.Provider);
        Assert.Equal("001234.abcdef0123456789.0123", identity.SubjectId);
        Assert.Equal("abc123xyz@privaterelay.appleid.com", identity.Email);
        Assert.True(identity.EmailVerified);
    }

    [Fact]
    public async Task Apple_s_email_verified_as_a_string_counts_too() =>
        Assert.True((await Sut().VerifyAsync(Token(emailVerified: "true"), RawNonce)).EmailVerified);

    [Fact]
    public async Task A_token_for_another_app_is_refused() =>
        await Refused(Token(audience: "com.someone.else"));

    [Fact]
    public async Task A_token_from_another_issuer_is_refused() =>
        await Refused(Token(issuer: "https://appleid.example.com"));

    [Fact]
    public async Task An_expired_token_is_refused() =>
        await Refused(Token(expires: DateTime.UtcNow.AddMinutes(-10)));

    [Fact]
    public async Task A_token_signed_with_someone_else_s_key_is_refused() =>
        await Refused(Token(key: SomeoneElsesKey));

    [Fact]
    public async Task A_token_whose_nonce_is_not_the_hash_of_the_sent_one_is_refused() =>
        await Refused(Token(nonce: Hash("a different nonce")));

    [Fact]
    public async Task A_token_with_the_raw_nonce_instead_of_its_hash_is_refused() =>
        await Refused(Token(nonce: RawNonce));

    [Fact]
    public async Task A_token_with_no_nonce_is_refused() =>
        await Refused(Token(omitNonce: true));

    [Fact]
    public async Task A_sign_in_that_sends_no_nonce_is_refused() =>
        await Refused(Token(), rawNonce: null);

    [Fact]
    public async Task A_later_sign_in_without_an_email_is_accepted_with_none()
    {
        var identity = await Sut().VerifyAsync(Token(email: null), RawNonce);

        Assert.Equal("", identity.Email);
        Assert.False(identity.EmailVerified);
    }

    [Fact]
    public async Task An_email_apple_says_is_unverified_is_refused()
    {
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().VerifyAsync(Token(emailVerified: "false"), RawNonce));
        Assert.Equal("oauth_email_unverified", ex.ErrorCode);
    }

    [Fact]
    public void Apple_is_offered_by_the_app_only_and_reports_the_bundle_id()
    {
        var sut = Sut();
        Assert.True(sut.AppOnly);
        Assert.Equal(BundleId, sut.Descriptor().ClientId);
        Assert.False(Sut(clientId: null).IsConfigured);
    }

    private static async Task Refused(string token, string? rawNonce = RawNonce)
    {
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().VerifyAsync(token, rawNonce));
        Assert.Equal("oauth_invalid_token", ex.ErrorCode);
    }

    /// <summary>Serves the public half of <see cref="AppleKey"/> as Apple's /auth/keys.</summary>
    private sealed class JwksHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https://apple.test/auth/keys", request.RequestUri!.ToString());
            var jwk = JsonWebKeyConverter.ConvertFromRSASecurityKey(new RsaSecurityKey(AppleKey.ExportParameters(false)) { KeyId = KeyId });
            jwk.Use = "sig";
            jwk.Alg = SecurityAlgorithms.RsaSha256;
            var body = JsonSerializer.Serialize(new { keys = new[] { jwk } });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
