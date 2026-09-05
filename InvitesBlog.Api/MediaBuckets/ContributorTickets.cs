using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Api.MediaBuckets;

/// <summary>
/// What somebody who scanned a QR code holds while they are adding to a bucket.
///
/// <para><b>Why a ticket and not a session.</b> A contributor is not a user of this product. They are
/// at a party, they pointed a camera at a card on a table, and the only thing they are going to do is
/// put some photographs somewhere. Giving them an account would be wrong; making them prove
/// themselves again for every photo would be worse — a phone picker hands back twenty files and
/// nobody is typing twenty codes. So one admission is minted once and carried for the evening.</para>
///
/// <para>It carries only what crediting a photo needs: which code admitted them, what to call them,
/// and — when the code demanded it — the contact they proved. Nothing about it authorizes reading the
/// bucket, and nothing about it survives the code being revoked, because
/// <c>IMediaBucketService.AdmitAsync</c> is still consulted on every upload.</para>
///
/// <para>Built exactly like <see cref="Rendering.RenderTickets"/>: HMAC over the JWT signing key
/// under its own context string, so a ticket from one purpose can never be replayed as another. No
/// new secret and no new table.</para>
/// </summary>
public sealed class ContributorTickets(IConfiguration config)
{
    private const string Context = "bucket-contributor:";

    /// <summary>
    /// Long enough to cover the event that produced it, short enough that a phone left on a table
    /// stops being a way into somebody's bucket a week later.
    /// </summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(12);

    /// <summary>The form field an upload carries it in.</summary>
    public const string FieldName = "ticket";

    private byte[] Key => Encoding.UTF8.GetBytes(
        config["Jwt:SigningKey"] ?? "change-this-in-production-please-use-a-long-random-value");

    public string Issue(Guid qrId, string displayName, string? verifiedContact, DateTimeOffset now)
    {
        // The separator cannot appear in a field, or a display name could forge the contact beside it
        // — "Bob|someone@example.com" would otherwise parse as a verified Bob. Escaped, not rejected:
        // a name is somebody's name and refusing a character in it is not the fix.
        var body = string.Join('|',
            qrId.ToString("N"),
            Escape(displayName),
            Escape(verifiedContact ?? string.Empty));

        var stamped = $"{Base64Url(Encoding.UTF8.GetBytes(body))}.{now.Add(Lifetime).ToUnixTimeSeconds()}";
        return $"{stamped}.{Base64Url(Sign(stamped))}";
    }

    /// <summary>
    /// What a ticket admits, or null if it is malformed, expired, or not signed by us. Never throws:
    /// this reads a value posted by a browser, and those arrive from anywhere.
    /// </summary>
    public ContributorAdmission? Read(string? ticket, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(ticket)) return null;

        var parts = ticket.Split('.');
        if (parts.Length != 3) return null;
        if (!long.TryParse(parts[1], out var expires)) return null;

        // Signature first, so an unsigned value never gets far enough to have its claims read.
        var expected = Sign($"{parts[0]}.{parts[1]}");
        var presented = FromBase64Url(parts[2]);
        if (presented is null || !CryptographicOperations.FixedTimeEquals(expected, presented)) return null;
        if (expires <= now.ToUnixTimeSeconds()) return null;

        var body = FromBase64Url(parts[0]);
        if (body is null) return null;

        var fields = Encoding.UTF8.GetString(body).Split('|');
        if (fields.Length != 3) return null;
        if (!Guid.TryParseExact(fields[0], "N", out var qrId)) return null;

        var contact = Unescape(fields[2]);
        return new ContributorAdmission(
            qrId,
            Unescape(fields[1]),
            string.IsNullOrEmpty(contact) ? null : contact);
    }

    private byte[] Sign(string body) =>
        HMACSHA256.HashData(Key, Encoding.UTF8.GetBytes(Context + body));

    private static string Escape(string value) => value.Replace("\\", @"\\").Replace("|", @"\|");

    private static string Unescape(string value) => value.Replace(@"\|", "|").Replace(@"\\", "\\");

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(string value)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            return Convert.FromBase64String(padded.PadRight((padded.Length + 3) / 4 * 4, '='));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}

/// <summary>Who a contributor ticket says is adding, and under which code.</summary>
/// <param name="VerifiedContact">
/// The email or phone they proved, or null when the code allowed anonymous contribution. This is the
/// difference between a credit that means something and one that is only what somebody typed.
/// </param>
public sealed record ContributorAdmission(Guid QrId, string DisplayName, string? VerifiedContact);
