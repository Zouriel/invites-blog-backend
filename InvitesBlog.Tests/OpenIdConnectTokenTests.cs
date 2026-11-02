using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
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

    private const string IosClientId = "ios-client-id.apps.googleusercontent.com";

    private static GoogleAuthProvider Sut(string? additionalClientIds = null, ILogger<GoogleAuthProvider>? logger = null)
    {
        var config = Substitute.For<IConfiguration>();
        config["OAuth:Google:ClientId"].Returns(ClientId);
        config["OAuth:Google:AdditionalClientIds"].Returns(additionalClientIds);

        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient().Returns(_ => new HttpClient(new JwksHandler()));
        return new GoogleAuthProvider(config, factory, logger);
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
    public async Task Google_s_picture_is_passed_on()
    {
        var token = Sign(new Dictionary<string, object>
        {
            ["sub"] = "1", ["email"] = "someone@example.com", ["email_verified"] = "true",
            ["picture"] = "https://lh3.googleusercontent.com/a/ACg8oc=s96-c"
        });

        Assert.Equal("https://lh3.googleusercontent.com/a/ACg8oc=s96-c", (await Sut().VerifyAsync(token)).PictureUrl);
    }

    [Fact]
    public async Task A_picture_that_is_not_https_is_dropped()
    {
        var token = Sign(new Dictionary<string, object>
        {
            ["sub"] = "1", ["email"] = "someone@example.com", ["email_verified"] = "true",
            ["picture"] = "http://lh3.googleusercontent.com/a/ACg8oc"
        });

        Assert.Null((await Sut().VerifyAsync(token)).PictureUrl);
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

    private static string VerifiedToken(string audience) => Sign(new Dictionary<string, object>
    {
        ["sub"] = "1", ["email"] = "someone@example.com", ["email_verified"] = "true"
    }, audience: audience);

    [Fact]
    public async Task A_token_for_the_ios_client_is_accepted_once_that_client_is_configured()
    {
        // An iPhone's native Google sign-in returns a token addressed to the iOS client id.
        var identity = await Sut($"{IosClientId}, other.apps.googleusercontent.com").VerifyAsync(VerifiedToken(IosClientId));

        Assert.Equal("someone@example.com", identity.Email);
    }

    [Fact]
    public async Task A_token_for_the_ios_client_is_refused_when_it_is_not_configured()
    {
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().VerifyAsync(VerifiedToken(IosClientId)));
        Assert.Equal("oauth_invalid_token", ex.ErrorCode);
    }

    [Fact]
    public async Task Extra_clients_do_not_open_the_door_to_someone_elses_app()
    {
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() =>
            Sut(IosClientId).VerifyAsync(VerifiedToken("someone-elses-app.apps.googleusercontent.com")));
        Assert.Equal("oauth_invalid_token", ex.ErrorCode);
    }

    [Fact]
    public async Task The_web_client_still_works_alongside_extra_clients_and_is_the_one_handed_out()
    {
        var sut = Sut(IosClientId);

        Assert.Equal("someone@example.com", (await sut.VerifyAsync(VerifiedToken(ClientId))).Email);
        // Browsers and Android are given the web client id, never the iOS one.
        Assert.Equal(ClientId, sut.Descriptor().ClientId);
    }

    [Fact]
    public async Task The_log_says_which_client_a_token_was_accepted_or_refused_for_and_nothing_personal()
    {
        var log = new ListLogger();
        var sut = Sut(IosClientId, log);

        await sut.VerifyAsync(VerifiedToken(IosClientId));
        await Assert.ThrowsAsync<UnauthorizedException>(() => sut.VerifyAsync(VerifiedToken("someone-elses-app.apps.googleusercontent.com")));

        Assert.Equal(2, log.Lines.Count);
        Assert.Equal((LogLevel.Information, $"google sign-in token accepted for client {IosClientId}"), log.Lines[0]);
        Assert.Equal(LogLevel.Warning, log.Lines[1].Level);
        Assert.Contains("refused", log.Lines[1].Text);
        Assert.EndsWith("for client someone-elses-app.apps.googleusercontent.com", log.Lines[1].Text);
        Assert.DoesNotContain(log.Lines, l => l.Text.Contains("someone@example.com"));
    }

    private sealed class ListLogger : ILogger<GoogleAuthProvider>
    {
        public List<(LogLevel Level, string Text)> Lines { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add((logLevel, formatter(state, exception)));
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
