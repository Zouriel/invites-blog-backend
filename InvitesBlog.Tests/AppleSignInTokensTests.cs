using System.Net;
using System.Security.Cryptography;
using InvitesBlog.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Apple's token endpoints are called as the client a code or token belongs to (the app or the website),
/// and never as a client that isn't ours. Apple's side of it is tested against a stand-in in
/// <c>AppleSignInTests</c> / <c>AppleWebSignInTests</c>.
/// </summary>
public class AppleSignInTokensTests
{
    private static (AppleSignInTokens Sut, List<string> Sent) Sut()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OAuth:Apple:ClientId"] = "blog.invites.app",
            ["OAuth:Apple:WebClientId"] = "blog.invites.web",
            ["OAuth:Apple:TeamId"] = "TEAM000001",
            ["OAuth:Apple:KeyId"] = "KEY0000001",
            ["OAuth:Apple:PrivateKey"] = key.ExportPkcs8PrivateKeyPem().Replace("\n", "\\n"),
            ["OAuth:Apple:BaseUrl"] = "https://apple.test",
        }).Build();
        var sent = new List<string>();
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(new Recorder(sent)));
        return (new AppleSignInTokens(config, http, NullLogger<AppleSignInTokens>.Instance), sent);
    }

    [Fact]
    public async Task Codes_are_exchanged_as_their_own_client_and_never_as_a_stranger()
    {
        var (sut, sent) = Sut();

        Assert.Equal("r.refresh", await sut.ExchangeAsync("c1"));
        Assert.Equal("r.refresh", await sut.ExchangeAsync("c2", "blog.invites.web"));
        Assert.Null(await sut.ExchangeAsync("c3", "com.someone.else"));

        Assert.Equal(2, sent.Count);
        Assert.Contains("client_id=blog.invites.app", sent[0]);
        Assert.Contains("client_id=blog.invites.web", sent[1]);
    }

    [Fact]
    public async Task Tokens_are_revoked_as_their_own_client_and_a_stranger_s_is_refused()
    {
        var (sut, sent) = Sut();

        await sut.RevokeAsync("t1", "blog.invites.web");
        await Assert.ThrowsAsync<InvalidOperationException>(() => sut.RevokeAsync("t2", "com.someone.else"));

        Assert.Single(sent);
        Assert.Contains("client_id=blog.invites.web", sent[0]);
    }

    private sealed class Recorder(List<string> sent) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            sent.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"refresh_token":"r.refresh"}"""),
            };
        }
    }
}
