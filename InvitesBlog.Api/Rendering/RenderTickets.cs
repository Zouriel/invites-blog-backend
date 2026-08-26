using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Api.Rendering;

/// <summary>
/// The three opaque strings the server-rendered guest path runs on.
///
/// A <b>render id</b> names an invitation in a URL without authorizing anything. A <b>ticket</b> is
/// the cookie that authorizes. A <b>handoff</b> carries admission across a host boundary, once.
///
/// Keeping authorization out of the URL is the whole design: a template may carry its own
/// JavaScript, and a top-level document can read its own <c>location.href</c> and navigate away with
/// it — measured, the sandbox flags do NOT stop that (see
/// <see cref="InvitesBlog.TemplateCompiler.TemplateRuntime.ContentSecurityPolicy"/>). So nothing that
/// grants access ever goes in the address bar of a rendered invitation.
///
/// All three are keyed off the JWT signing key, under separate contexts so one can never be replayed
/// as another. No new secret, and no new table: a render id is derived, not stored.
/// </summary>
public sealed class RenderTickets(IConfiguration config)
{
    private const string RenderIdContext = "render-id:";
    private const string TicketContext = "render-ticket:";
    private const string HandoffContext = "render-handoff:";

    /// <summary>How long a guest stays admitted before their link has to admit them again.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromDays(7);

    /// <summary>
    /// How long a cross-host handoff is good for. Seconds, because it exists only for the duration of
    /// one redirect: the browser follows it immediately, and what it turns into is a cookie.
    /// </summary>
    public static readonly TimeSpan HandoffLifetime = TimeSpan.FromSeconds(60);

    /// <summary>
    /// How many invitations one browser stays admitted to at once. A guest can hold several — a
    /// couple invited to two weddings — and admitting them to the newest must not evict the one they
    /// were reading a minute ago. Oldest drops out first.
    /// </summary>
    public const int MaxAdmitted = 8;

    /// <summary>The cookie name. Host-only and HttpOnly, so template JavaScript can never read it.</summary>
    public const string CookieName = "ib_invite";

    private byte[] Key => Encoding.UTF8.GetBytes(
        config["Jwt:SigningKey"] ?? "change-this-in-production-please-use-a-long-random-value");

    /// <summary>
    /// The stable, opaque id an invitation is served under. Derived rather than stored so a refresh,
    /// a back button, or reopening the original link all land on the same URL.
    /// </summary>
    public string RenderId(Guid inviteId)
    {
        var mac = HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(RenderIdContext + inviteId.ToString("N")));
        return Base64Url(mac.AsSpan(0, 16).ToArray());
    }

    /// <summary>
    /// Mints the cookie admitting this browser to <paramref name="inviteIds"/>. Order matters: the
    /// most recently admitted comes first and survives longest.
    /// </summary>
    public string IssueTicket(IEnumerable<Guid> inviteIds, DateTimeOffset now)
    {
        var ids = inviteIds.Distinct().Take(MaxAdmitted).ToList();
        if (ids.Count == 0) throw new ArgumentException("A ticket admits at least one invitation.", nameof(inviteIds));

        var expires = now.Add(TicketLifetime).ToUnixTimeSeconds();
        var body = $"{string.Join(',', ids.Select(i => i.ToString("N")))}.{expires}";
        return $"{body}.{Base64Url(Sign(TicketContext, body))}";
    }

    /// <summary>Adds an invitation to whatever this browser was already admitted to.</summary>
    public string Admit(string? existingTicket, Guid inviteId, DateTimeOffset now) =>
        IssueTicket(new[] { inviteId }.Concat(ReadTicket(existingTicket, now)), now);

    /// <summary>
    /// The invitations a ticket admits, or empty if it is malformed, expired, or not signed by us.
    /// Never throws: this parses a cookie, and cookies arrive from anywhere.
    /// </summary>
    public IReadOnlyList<Guid> ReadTicket(string? ticket, DateTimeOffset now)
    {
        var body = Verify(TicketContext, ticket, now);
        if (body is null) return [];

        var ids = new List<Guid>();
        foreach (var part in body.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            // One unreadable entry must not discard the rest — a guest's other invitations are still
            // legitimately theirs.
            if (Guid.TryParseExact(part, "N", out var id)) ids.Add(id);
        }
        return ids;
    }

    /// <summary>
    /// Mints a single-hop admission for another host to redeem. Used where the app holding the
    /// session and the app rendering the invitation are different origins, so a cookie cannot simply
    /// be set — a cookie's Domain may name the setting host or a parent, never a sibling.
    /// </summary>
    public string IssueHandoff(Guid inviteId, DateTimeOffset now)
    {
        var body = $"{inviteId:N}.{now.Add(HandoffLifetime).ToUnixTimeSeconds()}";
        return $"{body}.{Base64Url(Sign(HandoffContext, body))}";
    }

    /// <summary>Reads a handoff back, or null if it is malformed, expired, or not ours.</summary>
    public Guid? ReadHandoff(string? handoff, DateTimeOffset now)
    {
        var body = Verify(HandoffContext, handoff, now);
        return body is not null && Guid.TryParseExact(body, "N", out var id) ? id : null;
    }

    /// <summary>
    /// Shared shape for both signed values: <c>{payload}.{expiry}.{signature}</c>. Returns the
    /// payload only when the signature is ours AND the expiry is in the future — signature first, so
    /// an unsigned value never gets far enough to have its claims read.
    /// </summary>
    private string? Verify(string context, string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var parts = value.Split('.');
        if (parts.Length != 3) return null;
        if (!long.TryParse(parts[1], out var expires)) return null;

        var expected = Sign(context, $"{parts[0]}.{parts[1]}");
        var presented = FromBase64Url(parts[2]);
        if (presented is null || !CryptographicOperations.FixedTimeEquals(expected, presented)) return null;

        return expires > now.ToUnixTimeSeconds() ? parts[0] : null;
    }

    private byte[] Sign(string context, string body) =>
        HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(context + body));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string value)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + (4 - padded.Length % 4) % 4, '=');
            return Convert.FromBase64String(padded);
        }
        catch (FormatException) { return null; }
    }
}
