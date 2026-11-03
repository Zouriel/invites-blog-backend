using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("apple-signin")]
public sealed class AppleSignInCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// Sign in with Apple through the real API on real Postgres, with Apple replaced by
/// <see cref="AppleIdStub"/> over a socket: the identity token's checks, the code exchange, the sealed
/// refresh token, and its revocation when the account is deleted (App Store 5.1.1(v)).
/// </summary>
[Collection("apple-signin")]
public sealed class AppleSignInTests(PostgresApiFixture api)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private sealed record Envelope<T>(T Data);
    private sealed record Account(Guid Id, string? Email, string? DisplayName);
    private sealed record AuthResult(string Token, Account Account);
    private sealed record CodeDto(string ChallengeId);

    [PostgresFact]
    public async Task Apple_is_listed_for_the_iphone_only()
    {
        var web = await OptionsAsync("/api/auth/options");
        var android = await OptionsAsync("/api/auth/options?platform=android");
        var ios = await OptionsAsync("/api/auth/options?platform=ios");

        Assert.DoesNotContain("\"apple\"", web);
        Assert.DoesNotContain("\"apple\"", android);
        Assert.Contains("\"provider\":\"apple\"", ios);
        Assert.Contains("\"clientId\":\"blog.invites.app\"", ios);
    }

    [PostgresFact]
    public async Task First_sign_in_creates_the_account_keeps_the_token_sealed_and_deleting_it_revokes_at_apple()
    {
        var sub = $"001234.{Guid.NewGuid():N}.0001";
        var email = $"r{Guid.NewGuid():N}@privaterelay.appleid.com";
        var code = $"c{Guid.NewGuid():N}";
        var refresh = $"r{Guid.NewGuid():N}.0.refresh";
        api.Apple.Codes[code] = refresh;
        var client = api.Client();

        // First sign-in: email, name and code arrive.
        var first = await SignInAsync(client, api.Apple.IdentityToken(sub, email, "nonce-1"), "nonce-1", code, "Aisha", "Ibrahim");
        Assert.Equal(HttpStatusCode.OK, first.Status);
        Assert.Equal(email, first.Result!.Account.Email);
        Assert.Equal("Aisha Ibrahim", first.Result.Account.DisplayName);
        Assert.False(api.Apple.Codes.ContainsKey(code));   // exchanged at "Apple"

        var stored = await api.ReadAsync(db => db.UserExternalLogins.SingleAsync(l => l.Provider == "apple" && l.ExternalSubjectId == sub));
        Assert.NotNull(stored.RefreshTokenSealed);
        Assert.DoesNotContain(refresh, stored.RefreshTokenSealed);
        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains(refresh) || l.Contains(code));

        // A later sign-in: no email, a new code. Same account; the kept token isn't replaced.
        var code2 = $"c{Guid.NewGuid():N}";
        api.Apple.Codes[code2] = "should-not-be-used";
        var again = await SignInAsync(client, api.Apple.IdentityToken(sub, null, "nonce-2"), "nonce-2", code2);
        Assert.Equal(HttpStatusCode.OK, again.Status);
        Assert.Equal(first.Result.Account.Id, again.Result!.Account.Id);
        Assert.True(api.Apple.Codes.ContainsKey(code2));

        // Delete the account. Apple is down for the first try; the removal is retried.
        var me = api.Client(again.Result.Token);
        var sent = await me.PostAsync("/api/auth/me/deletion/request", null);
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
        var challenge = (await sent.Content.ReadFromJsonAsync<Envelope<CodeDto>>(Web))!.Data;
        var done = await me.PostAsJsonAsync("/api/auth/me/deletion/confirm", new { challengeId = challenge.ChallengeId, code = api.Codes.CodeFor(email) });
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
        Assert.False(await api.ReadAsync(db => db.UserExternalLogins.AnyAsync(l => l.ExternalSubjectId == sub)));

        api.Apple.FailNextRevoke = true;
        await api.RunRemovalsAsync();
        Assert.DoesNotContain(refresh, api.Apple.Revoked);
        var pending = await api.ReadAsync(db => db.PendingRemovals.SingleAsync(r => r.Kind == PendingRemovalKinds.AppleSignIn));
        Assert.DoesNotContain(refresh, pending.Target);

        await api.WriteAsync(async db =>
        {
            var row = await db.PendingRemovals.SingleAsync(r => r.Id == pending.Id);
            row.NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        });
        await api.RunRemovalsAsync();
        Assert.Contains(refresh, api.Apple.Revoked);
        Assert.False(await api.ReadAsync(db => db.PendingRemovals.AnyAsync(r => r.Id == pending.Id)));
        Assert.Equal(0, api.Apple.SecretsRefused);
        Assert.DoesNotContain(api.Logs.Lines, l => l.Contains(refresh));
    }

    /// <summary>Web sign-in is off unless switched on (this server leaves it off).</summary>
    [PostgresFact]
    public async Task With_web_sign_in_off_the_website_endpoints_refuse_and_a_website_token_is_refused()
    {
        var browser = api.BrowserClient();

        var start = await browser.PostAsync("/api/auth/apple/web/start", null);
        Assert.False(start.IsSuccessStatusCode);
        Assert.Contains("isn't available on the website", await start.Content.ReadAsStringAsync());
        Assert.False(start.Headers.Contains("Set-Cookie"));

        var callback = await browser.PostAsync("/auth/apple/callback",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["state"] = "s", ["id_token"] = "t" }));
        Assert.Equal(HttpStatusCode.SeeOther, callback.StatusCode);
        Assert.Equal("http://localhost/login?appleError=apple_web_off", callback.Headers.Location!.ToString());

        var redeem = await browser.PostAsJsonAsync("/api/auth/apple/web/redeem", new { code = "anything" });
        Assert.False(redeem.IsSuccessStatusCode);
        Assert.Contains("isn't available on the website", await redeem.Content.ReadAsStringAsync());

        // A genuine token for the website's Services ID, straight to the app's endpoint: refused.
        var sub = $"001234.{Guid.NewGuid():N}.0009";
        var direct = await SignInAsync(api.Client(),
            api.Apple.IdentityTokenAsGiven(sub, $"r{Guid.NewGuid():N}@privaterelay.appleid.com", AppleIdStub.NonceHash("n"), AppleIdStub.WebClientId), "n");
        Assert.Equal(HttpStatusCode.Unauthorized, direct.Status);
        Assert.False(await api.ReadAsync(db => db.UserExternalLogins.AnyAsync(l => l.ExternalSubjectId == sub)));
    }

    [PostgresFact]
    public async Task A_token_without_the_nonce_it_was_made_for_signs_nobody_in()
    {
        var sub = $"001234.{Guid.NewGuid():N}.0002";
        var token = api.Apple.IdentityToken(sub, $"r{Guid.NewGuid():N}@privaterelay.appleid.com", "the-real-nonce");
        var client = api.Client();

        var wrong = await SignInAsync(client, token, "another-nonce");
        var none = await SignInAsync(client, token, null);

        Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, none.Status);
        Assert.False(await api.ReadAsync(db => db.UserExternalLogins.AnyAsync(l => l.ExternalSubjectId == sub)));
    }

    [PostgresFact]
    public async Task A_first_sign_in_without_an_email_creates_nothing()
    {
        var sub = $"001234.{Guid.NewGuid():N}.0003";
        var result = await SignInAsync(api.Client(), api.Apple.IdentityToken(sub, null, "n"), "n");

        Assert.Equal(HttpStatusCode.Unauthorized, result.Status);
        Assert.Contains("didn't share an email", result.Body);
        Assert.False(await api.ReadAsync(db => db.UserExternalLogins.AnyAsync(l => l.ExternalSubjectId == sub)));
    }

    [PostgresFact]
    public async Task A_code_apple_refuses_still_signs_in_with_no_token_kept()
    {
        var sub = $"001234.{Guid.NewGuid():N}.0004";
        var result = await SignInAsync(api.Client(), api.Apple.IdentityToken(sub, $"r{Guid.NewGuid():N}@privaterelay.appleid.com", "n"), "n", "a-code-apple-never-issued");

        Assert.Equal(HttpStatusCode.OK, result.Status);
        Assert.Null(await api.ReadAsync(db => db.UserExternalLogins.Where(l => l.ExternalSubjectId == sub).Select(l => l.RefreshTokenSealed).SingleAsync()));
    }

    private async Task<string> OptionsAsync(string url)
    {
        var res = await api.Client().GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        return await res.Content.ReadAsStringAsync();
    }

    private static async Task<(HttpStatusCode Status, AuthResult? Result, string Body)> SignInAsync(
        HttpClient client, string idToken, string? nonce, string? code = null, string? given = null, string? family = null)
    {
        var res = await client.PostAsJsonAsync("/api/auth/oauth/apple", new { idToken, nonce, authorizationCode = code, givenName = given, familyName = family });
        var body = await res.Content.ReadAsStringAsync();
        var result = res.IsSuccessStatusCode ? JsonSerializer.Deserialize<Envelope<AuthResult>>(body, Web)!.Data : null;
        return (res.StatusCode, result, body);
    }
}
