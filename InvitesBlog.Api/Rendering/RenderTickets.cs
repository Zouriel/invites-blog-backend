using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Api.Rendering;

/// <summary>
/// The two opaque strings the server-rendered guest path runs on.
///
/// A <b>render id</b> names an invitation in a URL without authorizing anything. A <b>ticket</b> is
/// the cookie that authorizes, and it never appears in a URL — which matters more here than usual: a
/// template may carry its own JavaScript, and a top-level document can read its own
/// <c>location.href</c> and navigate away with it. (Measured: the sandbox flags do NOT stop that; see
/// <see cref="InvitesBlog.TemplateCompiler.TemplateRuntime.ContentSecurityPolicy"/>.) So the rule is
/// absolute — nothing that grants access goes in the address bar.
///
/// Both are keyed off the JWT signing key, so no new secret to manage and no new table: a render id
/// is stable for an invitation (refresh and back both work) and unguessable without the key.
/// </summary>
public sealed class RenderTickets(IConfiguration config)
{
    private const string RenderIdContext = "render-id:";
    private const string TicketContext = "render-ticket:";

    /// <summary>How long a guest stays admitted before the link has to re-admit them.</summary>
    public static readonly TimeSpan TicketLifetime = TimeSpan.FromDays(7);

    /// <summary>The cookie name. Host-only, HttpOnly, so template JavaScript can never read it.</summary>
    public const string CookieName = "ib_invite";

    private byte[] Key => Encoding.UTF8.GetBytes(
        config["Jwt:SigningKey"] ?? "change-this-in-production-please-use-a-long-random-value");

    /// <summary>
    /// The stable, opaque id an invitation is served under. Derived rather than stored so that a
    /// refresh, a back button, or reopening the original link all land on the same URL.
    /// </summary>
    public string RenderId(Guid inviteId)
    {
        var mac = HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(RenderIdContext + inviteId.ToString("N")));
        return Base64Url(mac.AsSpan(0, 16).ToArray());
    }

    /// <summary>Mints the cookie value admitting this guest to this invitation until it expires.</summary>
    public string IssueTicket(Guid inviteId, DateTimeOffset now)
    {
        var expires = now.Add(TicketLifetime).ToUnixTimeSeconds();
        var body = $"{inviteId:N}.{expires}";
        return $"{body}.{Base64Url(Sign(body))}";
    }

    /// <summary>
    /// Reads a ticket back, or null if it is malformed, expired, or not signed by us. Never throws on
    /// bad input — this parses a cookie, and cookies arrive from anywhere.
    /// </summary>
    public Guid? ReadTicket(string? ticket, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return null;

        var parts = ticket.Split('.');
        if (parts.Length != 3) return null;
        if (!Guid.TryParseExact(parts[0], "N", out var inviteId)) return null;
        if (!long.TryParse(parts[1], out var expires)) return null;

        // Signature first, then expiry: an unsigned value should never get as far as being trusted
        // enough to have its claims read.
        var expected = Sign($"{parts[0]}.{parts[1]}");
        var presented = FromBase64Url(parts[2]);
        if (presented is null || !CryptographicOperations.FixedTimeEquals(expected, presented)) return null;

        return expires > now.ToUnixTimeSeconds() ? inviteId : null;
    }

    private byte[] Sign(string body) =>
        HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(TicketContext + body));

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
