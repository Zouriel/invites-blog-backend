using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// 401 versus 403. The apps end a session only on a 401, so an expired or unknown token answering 403
/// left people "signed in" with every page failing. These pin down which refusal each caller gets,
/// that both carry the API's envelope, and that the open endpoints Public may use still let a caller
/// with a stale token through.
/// </summary>
public class AppAuthenticationHandlerTests
{
    private const string Key = "a-test-signing-key-that-is-long-enough-for-hmac-sha256-0123456789";

    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();

    private static InviteeJwt Jwt(string key) => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SigningKey"] = key }).Build());

    /// <summary>Authenticates a request carrying <paramref name="authorization"/>, as the pipeline would.</summary>
    private async Task<(AppAuthenticationHandler Handler, HttpContext Context, AuthenticateResult Result)> Authenticate(
        string? authorization)
    {
        var options = Substitute.For<IOptionsMonitor<AuthenticationSchemeOptions>>();
        options.Get(Arg.Any<string>()).Returns(new AuthenticationSchemeOptions());

        var handler = new AppAuthenticationHandler(
            options, NullLoggerFactory.Instance, UrlEncoder.Default, _campaigns, Jwt(Key));

        var context = new DefaultHttpContext();
        context.Response.Body = new MemoryStream();
        if (authorization is not null) context.Request.Headers.Authorization = authorization;

        await handler.InitializeAsync(
            new AuthenticationScheme(AppAuthenticationHandler.SchemeName, null, typeof(AppAuthenticationHandler)),
            context);
        var result = await handler.AuthenticateAsync();
        // What the authorization middleware does before it refuses.
        context.User = result.Principal ?? new ClaimsPrincipal();
        return (handler, context, result);
    }

    private static JsonElement Body(HttpContext context)
    {
        context.Response.Body.Position = 0;
        return JsonDocument.Parse(context.Response.Body).RootElement.Clone();
    }

    private static bool HasPermission(HttpContext context, string permission) =>
        context.User.HasClaim(AppClaims.Permission, permission);

    // ----- who the caller is ------------------------------------------------------------------------

    [Fact]
    public async Task No_token_is_the_anonymous_public_caller()
    {
        var (_, context, result) = await Authenticate(null);

        Assert.True(result.Succeeded);
        Assert.False(context.User.Identity!.IsAuthenticated);
        Assert.True(HasPermission(context, Permissions.Dashboard.Read));
    }

    [Fact]
    public async Task A_valid_session_authenticates()
    {
        var token = Jwt(Key).Issue("email", "guest@test.local", TimeSpan.FromMinutes(10));

        var (_, context, _) = await Authenticate($"Bearer {token}");

        Assert.True(context.User.Identity!.IsAuthenticated);
    }

    [Fact]
    public async Task A_campaign_possession_token_still_grants_its_campaign()
    {
        var campaign = new Campaign { Id = Guid.NewGuid() };
        _campaigns.GetByAccessTokenHashAsync(TokenService.Hash("possession-token"), Arg.Any<CancellationToken>())
            .Returns(campaign);

        var (_, context, _) = await Authenticate("Bearer possession-token");

        Assert.True(context.User.Identity!.IsAuthenticated);
        Assert.Equal(campaign.Id.ToString(), context.User.FindFirstValue(AppClaims.CampaignId));
        Assert.True(context.User.IsInRole(Roles.Inviter));
    }

    /// <summary>
    /// A stale token must not lock somebody out of what anybody may open — a magic-link dashboard,
    /// the OTP request. It is resolved to Public; only the REFUSAL changes.
    /// </summary>
    [Fact]
    public async Task A_stale_token_can_still_use_what_the_public_may()
    {
        var (_, context, _) = await Authenticate("Bearer no-longer-a-campaign");

        Assert.False(context.User.Identity!.IsAuthenticated);
        Assert.True(HasPermission(context, Permissions.Dashboard.Read));
    }

    // ----- the refusal ------------------------------------------------------------------------------

    [Theory]
    [InlineData("Bearer no-longer-a-campaign")]
    [InlineData("Bearer eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.bm90LWEtc2lnbmF0dXJl")]
    public async Task A_token_that_authenticates_nobody_is_refused_with_401_and_the_envelope(string authorization)
    {
        var (handler, context, _) = await Authenticate(authorization);

        await handler.ForbidAsync(new AuthenticationProperties());

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Contains("invalid_token", context.Response.Headers.WWWAuthenticate.ToString());
        var body = Body(context);
        Assert.False(body.GetProperty("success").GetBoolean());
        Assert.Contains("session has expired", body.GetProperty("message").GetString());
    }

    /// <summary>The same token signed with a key the server no longer holds — a rotated key, or a forgery.</summary>
    [Fact]
    public async Task A_session_signed_with_another_key_is_refused_with_401()
    {
        var foreign = Jwt("a-completely-different-signing-key-that-is-also-long-enough-0123456789")
            .Issue("email", "guest@test.local", TimeSpan.FromMinutes(10));
        var (handler, context, _) = await Authenticate($"Bearer {foreign}");

        await handler.ForbidAsync(new AuthenticationProperties());

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    /// <summary>A valid session without the permission is a genuine "you may not": 403, never a sign-out.</summary>
    [Fact]
    public async Task A_valid_session_without_the_permission_is_refused_with_403_and_the_envelope()
    {
        var token = Jwt(Key).Issue("email", "guest@test.local", TimeSpan.FromMinutes(10));
        var (handler, context, _) = await Authenticate($"Bearer {token}");

        await handler.ForbidAsync(new AuthenticationProperties());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.Equal("You don't have permission to do that.", Body(context).GetProperty("message").GetString());
    }

    /// <summary>Unchanged for a caller with no token: still 403, now with a message instead of an empty body.</summary>
    [Fact]
    public async Task No_token_on_a_protected_endpoint_is_a_403_with_the_envelope()
    {
        var (handler, context, _) = await Authenticate(null);

        await handler.ForbidAsync(new AuthenticationProperties());

        Assert.Equal(StatusCodes.Status403Forbidden, context.Response.StatusCode);
        Assert.False(Body(context).GetProperty("success").GetBoolean());
    }

    [Fact]
    public async Task A_challenge_is_a_401_with_the_envelope()
    {
        var (handler, context, _) = await Authenticate(null);

        await handler.ChallengeAsync(new AuthenticationProperties());

        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
        Assert.Equal("Sign in to continue.", Body(context).GetProperty("message").GetString());
    }
}
