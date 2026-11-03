using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
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

    private const string WebClientId = "blog.invites.web";

    private static AppleAuthProvider Sut(string? clientId = BundleId, bool? webEnabled = null, ILogger<AppleAuthProvider>? logger = null)
    {
        var config = Substitute.For<IConfiguration>();
        config["OAuth:Apple:ClientId"].Returns(clientId);
        config["OAuth:Apple:BaseUrl"].Returns("https://apple.test");
        // As compose sets it: the website's Services ID is always named; the switch decides.
        config["OAuth:Apple:WebClientId"].Returns(WebClientId);
        config["Urls:InviterBase"].Returns("https://invites.blog");
        if (webEnabled is { } on) config["OAuth:Apple:WebEnabled"].Returns(on ? "true" : "false");
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient().Returns(_ => new HttpClient(new JwksHandler()));
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new JwksHandler()));
        return new AppleAuthProvider(config, factory, logger);
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

    // ----- The website (Services ID blog.invites.web) ---------------------------------------------

    [Fact]
    public async Task With_web_sign_in_on_a_token_for_the_website_is_accepted_and_says_which_client()
    {
        var log = new ListLogger();
        var identity = await Sut(webEnabled: true, logger: log).VerifyAsync(Token(audience: WebClientId), RawNonce);

        Assert.Equal("001234.abcdef0123456789.0123", identity.SubjectId);
        Assert.Equal(WebClientId, identity.Audience);
        Assert.Equal((LogLevel.Information, $"apple sign-in token accepted for client {WebClientId}"), log.Lines.Single());
    }

    [Fact]
    public async Task With_web_sign_in_off_a_token_for_the_website_is_refused_and_logged_as_such()
    {
        var log = new ListLogger();
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            Sut(webEnabled: false, logger: log).VerifyAsync(Token(audience: WebClientId), RawNonce));

        Assert.Equal("oauth_invalid_token", ex.ErrorCode);
        Assert.Equal(LogLevel.Warning, log.Lines.Single().Level);
        Assert.StartsWith("apple sign-in token refused", log.Lines.Single().Text);
        Assert.EndsWith($"for client {WebClientId}", log.Lines.Single().Text);
        // Unset is off too.
        await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().VerifyAsync(Token(audience: WebClientId), RawNonce));
    }

    [Fact]
    public async Task With_web_sign_in_on_the_app_still_works_and_an_unknown_client_is_still_refused()
    {
        var sut = Sut(webEnabled: true);

        Assert.Equal(BundleId, (await sut.VerifyAsync(Token(), RawNonce)).Audience);
        await Assert.ThrowsAsync<UnauthorizedException>(() => sut.VerifyAsync(Token(audience: "com.someone.else"), RawNonce));
    }

    /// <summary>
    /// The website sends Apple the nonce's hash and keeps the raw one, as the app does; Apple puts what it
    /// was given in the token as-is. So the token carries the hash and the raw nonce is what verifies it.
    /// Had the raw nonce been sent to Apple (so the token carries it as-is), the sign-in would be refused.
    /// </summary>
    [Fact]
    public async Task A_website_token_verifies_with_the_raw_nonce_whose_hash_apple_was_given()
    {
        var sut = Sut(webEnabled: true);

        await sut.VerifyAsync(Token(audience: WebClientId, nonce: Hash(RawNonce)), RawNonce);
        await Assert.ThrowsAsync<UnauthorizedException>(() => sut.VerifyAsync(Token(audience: WebClientId, nonce: RawNonce), RawNonce));
        await Assert.ThrowsAsync<UnauthorizedException>(() => sut.VerifyAsync(Token(audience: WebClientId, nonce: Hash(RawNonce)), Hash(RawNonce)));
    }

    [Fact]
    public void The_website_is_offered_apple_only_while_web_sign_in_is_on_and_android_never()
    {
        var off = Sut(webEnabled: false);
        Assert.Null(off.DescriptorFor(null));
        Assert.Null(off.DescriptorFor("web"));
        Assert.Equal(BundleId, off.DescriptorFor("ios")!.ClientId);

        var on = Sut(webEnabled: true);
        var website = on.DescriptorFor(null)!;
        Assert.Equal(WebClientId, website.ClientId);
        Assert.Equal("https://invites.blog/auth/apple/callback", website.RedirectUri);
        Assert.Equal(WebClientId, on.DescriptorFor("web")!.ClientId);
        Assert.Null(on.DescriptorFor("android"));
        Assert.Equal(BundleId, on.DescriptorFor("ios")!.ClientId);
        Assert.Null(on.DescriptorFor("ios")!.RedirectUri);

        Assert.Null(Sut(clientId: null, webEnabled: true).DescriptorFor(null));
    }

    private sealed class ListLogger : ILogger<AppleAuthProvider>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
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
