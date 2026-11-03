using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Api.Controllers;
using InvitesBlog.Domain.Entities;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvitesBlog.Tests.Integration;

/// <summary>The API with Sign in with Apple switched on for the website (Services ID blog.invites.web).</summary>
public sealed class AppleWebOnFixture : PostgresApiFixture
{
    protected override IReadOnlyDictionary<string, string> ExtraSettings { get; } = new Dictionary<string, string>
    {
        ["OAuth__Apple__WebEnabled"] = "true",
    };
}

[CollectionDefinition("apple-web-signin")]
public sealed class AppleWebSignInCollection : ICollectionFixture<AppleWebOnFixture>;

/// <summary>
/// The website's Sign in with Apple through the real API on real Postgres, with Apple replaced by
/// <see cref="AppleIdStub"/>: start (state + nonce in a cookie), Apple's form_post return, and the
/// one-time code the page redeems for its session. Apple's part (the browser at appleid.apple.com) is
/// played by the test: it reads the authorize URL and mints the token Apple would.
/// </summary>
[Collection("apple-web-signin")]
public sealed class AppleWebSignInTests(AppleWebOnFixture api)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Envelope<T>(T Data);
    private sealed record Account(Guid Id, string? Email, string? DisplayName);
    private sealed record AuthResult(string Token, Account Account);
    private sealed record Start(string AuthorizeUrl);
    private sealed record CodeDto(string ChallengeId);

    [PostgresFact]
    public async Task The_website_is_offered_apple_with_its_own_client_and_return_url()
    {
        var web = await api.Client().GetStringAsync("/api/auth/options");
        var android = await api.Client().GetStringAsync("/api/auth/options?platform=android");
        var ios = await api.Client().GetStringAsync("/api/auth/options?platform=ios");

        Assert.Contains("\"provider\":\"apple\"", web);
        Assert.Contains("\"clientId\":\"blog.invites.web\"", web);
        Assert.Contains("\"redirectUri\":\"http://localhost/auth/apple/callback\"", web);
        Assert.DoesNotContain("\"apple\"", android);
        Assert.Contains("\"clientId\":\"blog.invites.app\"", ios);
        Assert.DoesNotContain("blog.invites.web", ios);
    }

    [PostgresFact]
    public async Task A_first_sign_in_creates_the_account_hands_back_a_one_time_code_and_deleting_revokes_as_the_website()
    {
        var sub = $"001234.{Guid.NewGuid():N}.0101";
        var email = $"w{Guid.NewGuid():N}@privaterelay.appleid.com";
        var code = $"c{Guid.NewGuid():N}";
        var refresh = $"r{Guid.NewGuid():N}.0.web";
        api.Apple.Codes[code] = refresh;
        api.Apple.CodeClients[code] = AppleIdStub.WebClientId;
        var browser = api.BrowserClient();

        var (authorize, state, nonce) = await StartAsync(browser);
        Assert.Equal("blog.invites.web", authorize["client_id"]);
        Assert.Equal("http://localhost/auth/apple/callback", authorize["redirect_uri"]);
        Assert.Equal("form_post", authorize["response_mode"]);
        Assert.Equal("code id_token", authorize["response_type"]);
        Assert.Equal("name email", authorize["scope"]);
        Assert.Matches("^[0-9a-f]{64}$", nonce);   // the hash of a raw nonce only the server holds

        var back = await CallbackAsync(browser, state,
            api.Apple.IdentityTokenAsGiven(sub, email, nonce, AppleIdStub.WebClientId), code,
            """{"name":{"firstName":"Aisha","lastName":"Ibrahim"},"email":"ignored@example.com"}""");
        var handoff = HandoffCode(back);

        var first = await RedeemAsync(browser, handoff);
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(email, first.Result!.Account.Email);
        Assert.Equal("Aisha Ibrahim", first.Result.Account.DisplayName);
        Assert.False(api.Apple.Codes.ContainsKey(code));   // exchanged as the website at "Apple"

        // Once only: even replayed with the browser's own binding cookie (captured from the callback).
        var again = await RedeemAsync(browser, handoff);
        Assert.Equal(HttpStatusCode.Unauthorized, again.Status);
        var replay = api.BrowserClient();
        replay.DefaultRequestHeaders.Add("Cookie", BindingCookie(back));
        Assert.Equal(HttpStatusCode.Unauthorized, (await RedeemAsync(replay, handoff)).Status);

        Assert.Contains(api.Logs.Lines, l => l.Contains($"apple sign-in token accepted for client {AppleIdStub.WebClientId}"));
        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains(refresh) || l.Contains(code) || l.Contains(handoff) || l.Contains(first.Result.Token));

        // Deleting the account revokes the token as the client it was issued to.
        var me = api.Client(first.Result.Token);
        var sent = await me.PostAsync("/api/auth/me/deletion/request", null);
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
        var challenge = (await sent.Content.ReadFromJsonAsync<Envelope<CodeDto>>(Web))!.Data;
        var done = await me.PostAsJsonAsync("/api/auth/me/deletion/confirm", new { challengeId = challenge.ChallengeId, code = api.Codes.CodeFor(email) });
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
        await api.RunRemovalsAsync();
        Assert.Contains((refresh, AppleIdStub.WebClientId), api.Apple.RevokedAs);
        Assert.Equal(0, api.Apple.SecretsRefused);
    }

    [PostgresFact]
    public async Task The_same_email_signs_in_to_the_same_account_as_the_app()
    {
        var email = $"s{Guid.NewGuid():N}@privaterelay.appleid.com";
        var app = await api.Client().PostAsJsonAsync("/api/auth/oauth/apple", new
        {
            idToken = api.Apple.IdentityToken($"001234.{Guid.NewGuid():N}.0102", email, "app-nonce"),
            nonce = "app-nonce",
        });
        Assert.True(app.IsSuccessStatusCode, await app.Content.ReadAsStringAsync());
        var fromApp = (await app.Content.ReadFromJsonAsync<Envelope<AuthResult>>(Web))!.Data;

        var browser = api.BrowserClient();
        var (_, state, nonce) = await StartAsync(browser);
        var back = await CallbackAsync(browser, state,
            api.Apple.IdentityTokenAsGiven($"001234.{Guid.NewGuid():N}.0103", email, nonce, AppleIdStub.WebClientId), null, null);
        var fromWeb = await RedeemAsync(browser, HandoffCode(back));

        Assert.Equal(fromApp.Account.Id, fromWeb.Result!.Account.Id);
    }

    [PostgresFact]
    public async Task A_return_whose_state_doesnt_match_signs_nobody_in()
    {
        var sub = $"001234.{Guid.NewGuid():N}.0104";
        var browser = api.BrowserClient();
        var (_, _, nonce) = await StartAsync(browser);

        var back = await CallbackAsync(browser, "not-the-state",
            api.Apple.IdentityTokenAsGiven(sub, $"x{Guid.NewGuid():N}@privaterelay.appleid.com", nonce, AppleIdStub.WebClientId), null, null);

        Assert.Equal("http://localhost/login?appleError=apple_state", back.Headers.Location!.ToString());
        Assert.False(await api.ReadAsync(db => db.UserExternalLogins.AnyAsync(l => l.ExternalSubjectId == sub)));
    }

    [PostgresFact]
    public async Task A_return_to_a_browser_that_never_started_signs_nobody_in()
    {
        var sub = $"001234.{Guid.NewGuid():N}.0105";
        var (_, state, nonce) = await StartAsync(api.BrowserClient());

        // Someone else's browser (no state cookie) is handed the right state and token.
        var back = await CallbackAsync(api.BrowserClient(), state,
            api.Apple.IdentityTokenAsGiven(sub, $"x{Guid.NewGuid():N}@privaterelay.appleid.com", nonce, AppleIdStub.WebClientId), null, null);

        Assert.Equal("http://localhost/login?appleError=apple_state", back.Headers.Location!.ToString());
        Assert.False(await api.ReadAsync(db => db.UserExternalLogins.AnyAsync(l => l.ExternalSubjectId == sub)));
    }

    [PostgresFact]
    public async Task A_token_made_for_another_nonce_signs_nobody_in()
    {
        var sub = $"001234.{Guid.NewGuid():N}.0106";
        var browser = api.BrowserClient();
        var (_, state, nonce) = await StartAsync(browser);

        // The hash of some other raw nonce (e.g. a token lifted from another sign-in).
        var back = await CallbackAsync(browser, state,
            api.Apple.IdentityTokenAsGiven(sub, $"x{Guid.NewGuid():N}@privaterelay.appleid.com",
                AppleIdStub.NonceHash("someone-elses-nonce"), AppleIdStub.WebClientId), null, null);

        Assert.Equal("http://localhost/login?appleError=oauth_invalid_token", back.Headers.Location!.ToString());
        Assert.False(await api.ReadAsync(db => db.UserExternalLogins.AnyAsync(l => l.ExternalSubjectId == sub)));
    }

    [PostgresFact]
    public async Task A_one_time_code_is_useless_in_another_browser_and_is_spent_by_trying()
    {
        var browser = api.BrowserClient();
        var (_, state, nonce) = await StartAsync(browser);
        var back = await CallbackAsync(browser, state,
            api.Apple.IdentityTokenAsGiven($"001234.{Guid.NewGuid():N}.0107", $"x{Guid.NewGuid():N}@privaterelay.appleid.com", nonce, AppleIdStub.WebClientId), null, null);
        var handoff = HandoffCode(back);

        var elsewhere = await RedeemAsync(api.BrowserClient(), handoff);
        var here = await RedeemAsync(browser, handoff);

        Assert.Equal(HttpStatusCode.Unauthorized, elsewhere.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, here.Status);
    }

    [PostgresFact]
    public async Task Cancelling_at_apple_goes_back_to_sign_in()
    {
        var browser = api.BrowserClient();
        var (_, state, _) = await StartAsync(browser);

        var res = await browser.PostAsync("/auth/apple/callback", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["state"] = state,
            ["error"] = "user_cancelled_authorize",
        }));

        Assert.Equal(HttpStatusCode.SeeOther, res.StatusCode);
        Assert.Equal("http://localhost/login?appleError=apple_cancelled", res.Headers.Location!.ToString());
    }

    private static async Task<(Dictionary<string, string> Query, string State, string Nonce)> StartAsync(HttpClient browser)
    {
        var res = await browser.PostAsync("/api/auth/apple/web/start", null);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var setCookie = string.Join("\n", res.Headers.GetValues("Set-Cookie"));
        Assert.Contains(AppleWebSignInController.StateCookie, setCookie);
        Assert.Contains("samesite=none", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("secure", setCookie, StringComparison.OrdinalIgnoreCase);

        var url = new Uri((await res.Content.ReadFromJsonAsync<Envelope<Start>>(Web))!.Data.AuthorizeUrl);
        Assert.Equal("https://appleid.apple.com/auth/authorize", url.GetLeftPart(UriPartial.Path));
        var query = QueryHelpers.ParseQuery(url.Query).ToDictionary(kv => kv.Key, kv => kv.Value.ToString());
        return (query, query["state"], query["nonce"]);
    }

    /// <summary>Apple's form_post, as the browser sends it back to the return URL.</summary>
    private static async Task<HttpResponseMessage> CallbackAsync(
        HttpClient browser, string state, string idToken, string? code, string? user)
    {
        var form = new Dictionary<string, string> { ["state"] = state, ["id_token"] = idToken };
        if (code is not null) form["code"] = code;
        if (user is not null) form["user"] = user;
        var res = await browser.PostAsync("/auth/apple/callback", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.SeeOther, res.StatusCode);
        return res;
    }

    /// <summary>The binding cookie the callback set, as a Cookie header ("name=value").</summary>
    private static string BindingCookie(HttpResponseMessage back) =>
        back.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(AppleWebSignInController.HandoffCookie + "=", StringComparison.Ordinal))
            .Split(';')[0];

    private static string HandoffCode(HttpResponseMessage back)
    {
        var location = back.Headers.Location!.ToString();
        Assert.StartsWith("http://localhost/oauth/apple#code=", location);
        // The session token itself never goes in a URL.
        Assert.DoesNotContain("token", location, StringComparison.OrdinalIgnoreCase);
        return Uri.UnescapeDataString(location["http://localhost/oauth/apple#code=".Length..]);
    }

    private static async Task<(HttpStatusCode Status, AuthResult? Result)> RedeemAsync(HttpClient browser, string code)
    {
        var res = await browser.PostAsJsonAsync("/api/auth/apple/web/redeem", new { code });
        var body = await res.Content.ReadAsStringAsync();
        return (res.StatusCode, res.IsSuccessStatusCode ? JsonSerializer.Deserialize<Envelope<AuthResult>>(body, Web)!.Data : null);
    }
}
