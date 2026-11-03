using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Accounts;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Accounts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// Sign in with Apple on the website, as a full-page redirect (no popup, so it works where popups are
/// blocked: in-app browsers, many phones).
/// <list type="number">
/// <item><c>POST /api/auth/apple/web/start</c>: makes a state and a raw nonce, keeps both in a sealed,
/// HttpOnly cookie (SameSite=None: Apple's return is a cross-site POST, which a Lax cookie wouldn't
/// ride), and answers Apple's authorize URL with the nonce's SHA-256. Apple puts the nonce in the
/// identity token exactly as given, so the token carries the hash, and the existing verifier (raw nonce
/// hashed, compared with the token's) is used unchanged, as for the iPhone.</item>
/// <item><c>POST /auth/apple/callback</c> (the return URL registered with Apple; Caddy routes this one
/// path to the API): Apple posts <c>state</c>, <c>code</c>, <c>id_token</c> and, on the first sign-in only,
/// <c>user</c> (the name). The state must match the cookie, the token is verified with the cookie's
/// nonce, and the same sign-in as the app's runs (same account linking). The session token is NEVER put
/// in a URL: the browser is sent to <c>/oauth/apple#code=…</c> with a one-time code that only this
/// browser (a second cookie) can redeem, within two minutes.</item>
/// <item><c>POST /api/auth/apple/web/redeem</c>: swaps that code for the session, once.</item>
/// </list>
/// Everything refuses while web sign-in is off (<see cref="AppleWebSettings"/>).
/// </summary>
public sealed class AppleWebSignInController(
    IAccountService accounts,
    AppleWebSettings web,
    AppleWebHandoffs handoffs,
    IConfiguration config,
    ILogger<AppleWebSignInController> logger) : BaseApiController
{
    public const string StateCookie = "__Host-ib-apple-state";
    public const string HandoffCookie = "__Host-ib-apple-handoff";
    public const string RateLimitPolicy = "apple-web";
    private const string StatePurpose = "apple-web-state-v1";
    private static readonly TimeSpan StateLifetime = TimeSpan.FromMinutes(10);

    public sealed record StartResult(string AuthorizeUrl);
    public sealed record RedeemRequest(string? Code);

    private sealed record StateTicket(string S, string N, long E);

    [HttpPost("/api/auth/apple/web/start")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicy)]
    public IActionResult Start()
    {
        RequireOn();
        var state = Random();
        var rawNonce = Random();
        var ticket = new StateTicket(state, rawNonce, DateTimeOffset.UtcNow.Add(StateLifetime).ToUnixTimeSeconds());
        Response.Cookies.Append(StateCookie, TokenSeal.Seal(JsonSerializer.Serialize(ticket), Secret(), StatePurpose),
            new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                // Apple's return is a form POST from appleid.apple.com: only a SameSite=None cookie is sent with it.
                SameSite = SameSiteMode.None,
                Path = "/",
                MaxAge = StateLifetime,
                IsEssential = true,
            });

        var query = new Dictionary<string, string>
        {
            ["client_id"] = web.ClientId!,
            ["redirect_uri"] = web.RedirectUri,
            ["response_type"] = "code id_token",
            ["response_mode"] = "form_post",
            ["scope"] = "name email",
            ["state"] = state,
            ["nonce"] = NonceHash(rawNonce),
        };
        var url = $"{AppleAuthorizeUrl}?" + string.Join("&",
            query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        return Success(new StartResult(url));
    }

    /// <summary>Apple's form_post return. Always answers with a redirect into the website.</summary>
    [HttpPost("/auth/apple/callback")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicy)]
    [Consumes("application/x-www-form-urlencoded", "multipart/form-data")]
    public async Task<IActionResult> Callback(
        [FromForm(Name = "state")] string? state,
        [FromForm(Name = "code")] string? code,
        [FromForm(Name = "id_token")] string? idToken,
        [FromForm(Name = "user")] string? user,
        [FromForm(Name = "error")] string? error,
        CancellationToken ct)
    {
        if (!web.IsOn) return Fail("apple_web_off");

        var ticket = ReadTicket();
        // One use: whatever happens next, this state is spent.
        Response.Cookies.Delete(StateCookie, new CookieOptions { Secure = true, SameSite = SameSiteMode.None, Path = "/" });

        if (ticket is null)
        {
            logger.LogWarning("Apple web sign-in refused: no sign-in was started in this browser (state cookie missing, unreadable or expired).");
            return Fail("apple_state");
        }
        if (string.IsNullOrEmpty(state) || !FixedEquals(state, ticket.S))
        {
            logger.LogWarning("Apple web sign-in refused: the state doesn't match the one this browser started with.");
            return Fail("apple_state");
        }
        if (!string.IsNullOrEmpty(error))
            return Fail(error == "user_cancelled_authorize" ? "apple_cancelled" : "apple_error");
        if (string.IsNullOrWhiteSpace(idToken))
            return Fail("apple_error");

        var (given, family) = NameFrom(user);
        AuthResultDto result;
        try
        {
            result = await accounts.OAuthAsync("apple",
                new OAuthLoginRequest(idToken, ticket.N, string.IsNullOrWhiteSpace(code) ? null : code, given, family), ct);
        }
        catch (AppException ex)
        {
            // The provider has already logged why (accepted/refused for client …); no token is logged here.
            return Fail(ex.ErrorCode);
        }

        var binding = Random();
        var handoff = handoffs.Add(result, binding);
        Response.Cookies.Append(HandoffCookie, binding, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            MaxAge = AppleWebHandoffs.Lifetime,
            IsEssential = true,
        });
        // The fragment never reaches a server log or a Referer header.
        return Redirect303($"{config.InviterBase()}/oauth/apple#code={Uri.EscapeDataString(handoff)}");
    }

    /// <summary>Something opened the return URL by hand: back to sign-in.</summary>
    [HttpGet("/auth/apple/callback")]
    [AllowAnonymous]
    public IActionResult CallbackGet() => Fail("apple_error");

    [HttpPost("/api/auth/apple/web/redeem")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicy)]
    public IActionResult Redeem([FromBody] RedeemRequest request)
    {
        RequireOn();
        var binding = Request.Cookies[HandoffCookie];
        Response.Cookies.Delete(HandoffCookie, new CookieOptions { Secure = true, SameSite = SameSiteMode.Lax, Path = "/" });
        var result = handoffs.Take(request.Code, binding)
                     ?? throw new UnauthorizedException(
                         "That Apple sign-in has expired or was already used. Please sign in again.", "apple_handoff");
        return Success(result);
    }

    private const string AppleAuthorizeUrl = "https://appleid.apple.com/auth/authorize";

    private void RequireOn()
    {
        if (!web.IsOn)
            throw new BusinessRuleException("Sign in with Apple isn't available on the website yet.", "apple_web_off");
    }

    private IActionResult Fail(string reason) =>
        Redirect303($"{config.InviterBase()}/login?appleError={Uri.EscapeDataString(Word(reason))}");

    /// <summary>303 so the browser follows with a GET, not a re-POST of Apple's form.</summary>
    private IActionResult Redirect303(string url)
    {
        Response.Headers.CacheControl = "no-store";
        Response.Headers.Location = url;
        return StatusCode(StatusCodes.Status303SeeOther);
    }

    private StateTicket? ReadTicket()
    {
        var plain = TokenSeal.Open(Request.Cookies[StateCookie], Secret(), StatePurpose);
        if (plain is null) return null;
        try
        {
            var ticket = JsonSerializer.Deserialize<StateTicket>(plain);
            return ticket is null || ticket.E < DateTimeOffset.UtcNow.ToUnixTimeSeconds() ? null : ticket;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Apple's <c>user</c> field (first sign-in only): {"name":{"firstName","lastName"},"email"}.</summary>
    internal static (string? Given, string? Family) NameFrom(string? user)
    {
        if (string.IsNullOrWhiteSpace(user) || user.Length > 4096) return (null, null);
        try
        {
            using var doc = JsonDocument.Parse(user);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.Object)
                return (null, null);
            return (Str(name, "firstName"), Str(name, "lastName"));
        }
        catch (JsonException)
        {
            return (null, null);
        }

        static string? Str(JsonElement e, string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private string Secret() => config["Jwt:SigningKey"] ?? "change-this-in-production-please-use-a-long-random-value";

    internal static string NonceHash(string raw) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(raw)));

    private static string Random() => Base64Url(RandomNumberGenerator.GetBytes(32));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>Error words for the URL: short and plain, never anything Apple or a caller sent verbatim.</summary>
    private static string Word(string s) =>
        new(s.Where(c => char.IsAsciiLetterOrDigit(c) || c == '_').Take(40).ToArray());
}

/// <summary>
/// One-time codes handed to the browser after a web Apple sign-in, each redeemable once, by the browser
/// holding its binding cookie, within <see cref="Lifetime"/>. Kept in memory: the API runs as one
/// instance, and a restart only means signing in again. Codes are stored hashed.
/// </summary>
public sealed class AppleWebHandoffs(TimeProvider clock)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(2);

    private sealed record Entry(AuthResultDto Result, string BindingHash, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    public string Add(AuthResultDto result, string binding)
    {
        Prune();
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        entries[Hash(code)] = new Entry(result, Hash(binding), clock.GetUtcNow().Add(Lifetime));
        return code;
    }

    /// <summary>The sign-in for a code, once; null when unknown, used, expired, or another browser's.</summary>
    public AuthResultDto? Take(string? code, string? binding)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 200) return null;
        // Removed first: a code tried from another browser (wrong or no binding) is spent too, so a code
        // that leaked can't be tried at all once its own browser or anyone else has had a go.
        if (!entries.TryRemove(Hash(code), out var entry)) return null;
        if (string.IsNullOrEmpty(binding) || entry.Expires < clock.GetUtcNow()) return null;
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(entry.BindingHash), Encoding.UTF8.GetBytes(Hash(binding)))
            ? entry.Result
            : null;
    }

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (key, entry) in entries)
            if (entry.Expires < now) entries.TryRemove(key, out _);
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
