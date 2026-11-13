using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Api.Controllers;
using Xunit;

namespace InvitesBlog.Tests.Integration;

/// <summary>
/// The API as production runs it since the web app moved to its own host: the public site on one origin
/// (InviterBase), the signed-in app on another (AppBase), and web Apple sign-in on.
/// </summary>
public sealed class AppHostFixture : PostgresApiFixture
{
    public const string AppOrigin = "https://app.localhost";

    protected override IReadOnlyDictionary<string, string> ExtraSettings { get; } = new Dictionary<string, string>
    {
        ["Urls__AppBase"] = AppOrigin,
        ["OAuth__Apple__WebEnabled"] = "true",
    };
}

[CollectionDefinition("app-host")]
public sealed class AppHostCollection : ICollectionFixture<AppHostFixture>;

/// <summary>
/// Moving a sign-in from invites.blog to app.invites.blog (<see cref="SessionHandoffController"/>), and the
/// links and sign-in returns that now point at the app's own host.
/// </summary>
[Collection("app-host")]
public sealed class SessionHandoffTests(AppHostFixture api)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Envelope<T>(T Data);
    private sealed record Account(Guid Id, string? Email);
    private sealed record AuthResult(string Token, Account Account);
    private sealed record Handoff(string Code, int ExpiresInSeconds, string? Cookie = null);
    private sealed record Start(string AuthorizeUrl);

    private async Task<AuthResult> SignedInAsync()
    {
        var email = $"h{Guid.NewGuid():N}@privaterelay.appleid.com";
        var res = await api.Client().PostAsJsonAsync("/api/auth/oauth/apple", new
        {
            idToken = api.Apple.IdentityToken($"001234.{Guid.NewGuid():N}.0201", email, "n"),
            nonce = "n",
        });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<Envelope<AuthResult>>(Web))!.Data;
    }

    private async Task<(HttpStatusCode Status, Handoff? Handoff)> IssueAsync(string? token)
    {
        var res = await api.Client(token).PostAsync("/api/auth/handoff", null);
        var body = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return (res.StatusCode, null);
        // The browser keeps the binding cookie the API set and sends it back from the app host.
        var cookie = res.Headers.TryGetValues("Set-Cookie", out var set)
            ? set.Select(c => c.Split(';')[0]).FirstOrDefault(c => c.StartsWith(SessionHandoffController.BindingCookie + "="))
            : null;
        return (res.StatusCode, JsonSerializer.Deserialize<Envelope<Handoff>>(body, Web)!.Data with { Cookie = cookie });
    }

    private async Task<(HttpStatusCode Status, AuthResult? Result)> RedeemAsync(string code, string? cookie = null)
    {
        // A different client from the one that issued it, as when the code arrives in another host's page;
        // only the binding cookie (when given) carries over, as the browser does.
        var req = new HttpRequestMessage(HttpMethod.Post, "/api/auth/handoff/redeem") { Content = JsonContent.Create(new { code }) };
        if (cookie is not null) req.Headers.Add("Cookie", cookie);
        var res = await api.Client().SendAsync(req);
        var body = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, res.IsSuccessStatusCode ? JsonSerializer.Deserialize<Envelope<AuthResult>>(body, Web)!.Data : null);
    }

    [PostgresFact]
    public async Task A_signed_in_session_moves_once_to_the_app_host_and_nothing_secret_is_logged()
    {
        var site = await SignedInAsync();

        var (status, handoff) = await IssueAsync(site.Token);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(60, handoff!.ExpiresInSeconds);
        Assert.True(handoff.Code.Length >= 40);

        Assert.NotNull(handoff.Cookie);
        var moved = await RedeemAsync(handoff.Code, handoff.Cookie);
        Assert.Equal(HttpStatusCode.OK, moved.Status);
        Assert.Equal(site.Account.Id, moved.Result!.Account.Id);
        // The session it hands over is a working one.
        var me = await api.Client(moved.Result.Token).GetAsync("/api/auth/me");
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);

        // Once only.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(handoff.Code, handoff.Cookie)).Status);

        Assert.Contains(api.Logs.Lines, l => l.Contains($"session handoff issued for account {site.Account.Id}"));
        Assert.Contains(api.Logs.Lines, l => l.Contains($"session handoff redeemed for account {site.Account.Id}"));
        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains(handoff.Code) || l.Contains(moved.Result.Token) || l.Contains(site.Token));
    }

    [PostgresFact]
    public async Task A_code_only_signs_in_the_browser_it_was_issued_to()
    {
        // Someone issues a code for their own account and puts it in a link for another person: that
        // person's browser has no binding cookie (or a different one), so it is refused, and the code is spent.
        var attacker = await SignedInAsync();
        var (_, mine) = await IssueAsync(attacker.Token);
        var (_, other) = await IssueAsync(attacker.Token);
        Assert.NotNull(mine!.Cookie);
        Assert.NotEqual(mine.Cookie, other!.Cookie);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(mine.Code)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(other.Code, mine.Cookie)).Status);
        // Both were spent by the refused tries.
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(mine.Code, mine.Cookie)).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(other.Code, other.Cookie)).Status);
    }

    [Fact]
    public void The_binding_cookie_is_shared_with_the_app_subdomain_only()
    {
        Assert.Equal("invites.blog", SessionHandoffController.SharedCookieDomain("https://invites.blog", "https://app.invites.blog"));
        Assert.Null(SessionHandoffController.SharedCookieDomain("https://invites.blog", "https://invites.blog"));
        Assert.Null(SessionHandoffController.SharedCookieDomain("https://invites.blog", "https://evilinvites.blog"));
    }

    [PostgresFact]
    public async Task Nobody_signed_out_can_get_a_code_and_a_made_up_code_signs_nobody_in()
    {
        var (status, _) = await IssueAsync(null);
        Assert.Equal(HttpStatusCode.Unauthorized, status);

        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync("made-up-code")).Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync("")).Status);
    }

    [PostgresFact]
    public async Task Sign_in_returns_and_emailed_links_point_at_the_app_host_while_apple_keeps_its_registered_return_url()
    {
        var browser = api.BrowserClient();
        var start = await browser.PostAsync("/api/auth/apple/web/start", null);
        Assert.True(start.IsSuccessStatusCode, await start.Content.ReadAsStringAsync());
        var authorize = (await start.Content.ReadFromJsonAsync<Envelope<Start>>(Web))!.Data.AuthorizeUrl;
        // The return URL registered with Apple stays on the public site.
        Assert.Contains(Uri.EscapeDataString("http://localhost/auth/apple/callback"), authorize);

        var cancelled = await browser.PostAsync("/auth/apple/callback", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["state"] = "whatever",
            ["error"] = "user_cancelled_authorize",
        }));
        Assert.Equal(HttpStatusCode.SeeOther, cancelled.StatusCode);
        Assert.StartsWith($"{AppHostFixture.AppOrigin}/login?appleError=", cancelled.Headers.Location!.ToString());
    }

    [PostgresFact]
    public async Task Emailed_links_to_app_screens_use_the_app_host()
    {
        var reporter = await SignedInAsync();
        var subject = await SignedInAsync();
        var before = api.Mail.Sent.Count(m => m.To == PostgresApiFixture.ReportInbox);

        var report = await api.Client(reporter.Token).PostAsJsonAsync("/api/me/reports",
            new { kind = "user", targetId = subject.Account.Id, reason = "abusive" });
        Assert.True(report.IsSuccessStatusCode, await report.Content.ReadAsStringAsync());

        var mail = api.Mail.Sent.Where(m => m.To == PostgresApiFixture.ReportInbox).Skip(before).Last();
        Assert.Contains($"{AppHostFixture.AppOrigin}/admin/reports", mail.Html);
    }

    [PostgresFact]
    public async Task The_app_host_may_call_the_api_with_credentials()
    {
        var preflight = new HttpRequestMessage(HttpMethod.Options, "/api/auth/apple/web/redeem");
        preflight.Headers.Add("Origin", AppHostFixture.AppOrigin);
        preflight.Headers.Add("Access-Control-Request-Method", "POST");
        preflight.Headers.Add("Access-Control-Request-Headers", "content-type");
        var res = await api.Client().SendAsync(preflight);

        Assert.Equal(AppHostFixture.AppOrigin, res.Headers.GetValues("Access-Control-Allow-Origin").Single());
        Assert.Equal("true", res.Headers.GetValues("Access-Control-Allow-Credentials").Single());

        var stranger = new HttpRequestMessage(HttpMethod.Options, "/api/auth/apple/web/redeem");
        stranger.Headers.Add("Origin", "https://evil.example");
        stranger.Headers.Add("Access-Control-Request-Method", "POST");
        var refused = await api.Client().SendAsync(stranger);
        Assert.False(refused.Headers.Contains("Access-Control-Allow-Origin"));
    }
}
