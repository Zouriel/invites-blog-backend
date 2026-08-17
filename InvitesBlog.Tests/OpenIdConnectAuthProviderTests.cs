using InvitesBlog.Application.Exceptions;
using InvitesBlog.Infrastructure.Security;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Nothing the browser posts is trusted, so the failure modes matter as much as the success one:
/// rubbish in the token field is the CALLER's mistake and must read as one, not as a server fault.
/// </summary>
public class OpenIdConnectAuthProviderTests
{
    private static GoogleAuthProvider Sut(string? clientId = "client-id.apps.googleusercontent.com")
    {
        var config = Substitute.For<IConfiguration>();
        config["OAuth:Google:ClientId"].Returns(clientId);
        return new GoogleAuthProvider(config, Substitute.For<IHttpClientFactory>());
    }

    [Fact]
    public async Task A_token_that_is_not_a_jwt_is_rejected_as_unauthorized_not_a_server_error()
    {
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(
            () => Sut().VerifyAsync("not-a-jwt"));

        Assert.Equal("oauth_invalid_token", ex.ErrorCode);
    }

    [Fact]
    public async Task An_empty_token_is_rejected_before_anything_is_fetched()
    {
        var ex = await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().VerifyAsync("  "));
        Assert.Equal("oauth_invalid_token", ex.ErrorCode);
    }

    [Fact]
    public async Task An_unconfigured_provider_says_so_rather_than_failing_obscurely()
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut(clientId: null).VerifyAsync("anything"));

        Assert.Equal("oauth_not_configured", ex.ErrorCode);
    }

    [Fact]
    public void An_unconfigured_provider_reports_itself_as_unconfigured()
    {
        Assert.False(Sut(clientId: null).IsConfigured);
        Assert.True(Sut().IsConfigured);
    }
}
