using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Accounts;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Accounts;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// Carries a sign-in from the public site (invites.blog) to the web app (app.invites.blog), which since
/// 2026-11-13 live on different hosts. A browser keeps the session in the site's own storage, which the
/// other host can't read, so without this everyone signed in on invites.blog would arrive at the app
/// signed out.
/// <list type="number">
/// <item><c>POST /api/auth/handoff</c> (signed in, on invites.blog): a fresh session for the same account
/// is issued (the same checks as a refresh: an inactive account is refused) and parked behind a random
/// one-time code, which is answered. invites.blog then forgets its own copy and sends the browser to
/// <c>app.invites.blog/handoff#code=…</c>; the code is in the fragment, so it never reaches a server log
/// or a Referer header.</item>
/// <item><c>POST /api/auth/handoff/redeem</c> (anonymous, on app.invites.blog): swaps the code for that
/// session, once.</item>
/// </list>
/// A code works once and for <see cref="SessionHandoffs.Lifetime"/> (60 s), is kept only in memory and
/// hashed, and is never logged.
/// <para>A code also only works in the browser it was issued to: issuing sets an HttpOnly cookie (on the
/// shared parent domain, so app.invites.blog sends it back) holding a random binding, and redeeming needs
/// that cookie. Without it a code issued for one person's account could be put in a link that signs
/// someone else's browser in as them.</para>
/// </summary>
public sealed class SessionHandoffController(
    IAccountService accounts,
    SessionHandoffs handoffs,
    IConfiguration config,
    ILogger<SessionHandoffController> logger) : BaseApiController
{
    public const string RateLimitPolicy = "session-handoff";
    public const string BindingCookie = "ib_handoff";
    public const string CookiePath = "/api/auth/handoff";

    public sealed record HandoffResult(string Code, int ExpiresInSeconds);
    public sealed record RedeemRequest(string? Code);

    [HttpPost("/api/auth/handoff")]
    // "Signed in at all", like the other account endpoints (the controller is anonymous by default).
    [HasPermission(Permissions.Templates.Read)]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var session = await accounts.RefreshAsync(ct);
        var binding = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var code = handoffs.Add(session, binding)
                   ?? throw new BusinessRuleException("Too many sign-ins are being moved right now. Please try again in a minute.", "handoff_busy");
        Response.Cookies.Append(BindingCookie, binding, CookieOptions(SessionHandoffs.Lifetime));
        logger.LogInformation("session handoff issued for account {AccountId}", session.Account.Id);
        return Success(new HandoffResult(code, (int)SessionHandoffs.Lifetime.TotalSeconds));
    }

    [HttpPost("/api/auth/handoff/redeem")]
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicy)]
    public IActionResult Redeem([FromBody] RedeemRequest request)
    {
        var binding = Request.Cookies[BindingCookie];
        Response.Cookies.Delete(BindingCookie, CookieOptions(null));
        var session = handoffs.Take(request.Code, binding);
        if (session is null)
        {
            logger.LogInformation("session handoff refused: unknown, used or expired code");
            throw new UnauthorizedException("That sign-in link has expired. Please sign in again.", "handoff_expired");
        }
        logger.LogInformation("session handoff redeemed for account {AccountId}", session.Account.Id);
        return Success(session);
    }

    private CookieOptions CookieOptions(TimeSpan? maxAge) => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = CookiePath,
        Domain = SharedCookieDomain(config.InviterBase(), config.AppBase()),
        MaxAge = maxAge,
        IsEssential = true,
    };

    /// <summary>
    /// The domain both hosts share when the app lives on a subdomain of the public site (app.invites.blog
    /// under invites.blog): the cookie set on one is then sent to the other. Null (a host-only cookie) when
    /// they are the same host or unrelated.
    /// </summary>
    public static string? SharedCookieDomain(string siteBase, string appBase)
    {
        if (!Uri.TryCreate(siteBase, UriKind.Absolute, out var site) || !Uri.TryCreate(appBase, UriKind.Absolute, out var app)) return null;
        if (site.HostNameType != UriHostNameType.Dns || site.Host == app.Host) return null;
        return app.Host.EndsWith("." + site.Host, StringComparison.OrdinalIgnoreCase) ? site.Host : null;
    }
}

/// <summary>
/// The one-time codes behind <see cref="SessionHandoffController"/>: each holds a session for
/// <see cref="Lifetime"/> and can be taken once. In memory (the API runs as one instance; a restart only
/// means signing in again) and stored by the code's SHA-256, never the code itself.
/// </summary>
public sealed class SessionHandoffs(TimeProvider clock)
{
    /// <summary>How long a code works: long enough for one redirect, short enough to be useless later.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    /// <summary>Codes held at once, at most. Each is minutes of nothing to anyone, so this only stops a flood.</summary>
    public const int Capacity = 10_000;

    private sealed record Entry(AuthResultDto Session, string BindingHash, DateTimeOffset Expires);

    private readonly ConcurrentDictionary<string, Entry> entries = new(StringComparer.Ordinal);

    /// <summary>
    /// A new code for this session, redeemable only together with <paramref name="binding"/>; null when
    /// <see cref="Capacity"/> codes are already waiting.
    /// </summary>
    public string? Add(AuthResultDto session, string binding)
    {
        ArgumentException.ThrowIfNullOrEmpty(binding);
        Prune();
        if (entries.Count >= Capacity) return null;
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        entries[Hash(code)] = new Entry(session, Hash(binding), clock.GetUtcNow().Add(Lifetime));
        return code;
    }

    /// <summary>
    /// The session for a code, once; null when the code is unknown, used, expired, or presented without the
    /// binding it was issued with. A code tried with the wrong binding is spent too.
    /// </summary>
    public AuthResultDto? Take(string? code, string? binding)
    {
        if (string.IsNullOrEmpty(code) || code.Length > 200) return null;
        if (!entries.TryRemove(Hash(code), out var entry)) return null;
        if (string.IsNullOrEmpty(binding) || binding.Length > 200 || entry.Expires < clock.GetUtcNow()) return null;
        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(entry.BindingHash), Encoding.UTF8.GetBytes(Hash(binding)))
            ? entry.Session
            : null;
    }

    /// <summary>Codes waiting to be used (tests and the capacity check).</summary>
    public int Count => entries.Count;

    private void Prune()
    {
        var now = clock.GetUtcNow();
        foreach (var (key, entry) in entries)
            if (entry.Expires < now) entries.TryRemove(key, out _);
    }

    private static string Hash(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));
}
