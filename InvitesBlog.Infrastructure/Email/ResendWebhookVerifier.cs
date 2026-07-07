using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Infrastructure.Email;

/// <summary>
/// Verifies a Resend (Svix) webhook signature before we trust the payload (provider guide §2.6).
/// Signed content is <c>{svix-id}.{svix-timestamp}.{body}</c>, HMAC-SHA256 with the base64 secret
/// (the part after <c>whsec_</c>), compared constant-time against each <c>v1,</c> signature.
/// </summary>
public sealed class ResendWebhookVerifier(IConfiguration config)
{
    public bool Verify(string body, string? svixId, string? svixTimestamp, string? svixSignature)
    {
        var secret = config["Email:WebhookSecret"];
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(svixId)
            || string.IsNullOrEmpty(svixTimestamp) || string.IsNullOrEmpty(svixSignature))
            return false;

        var keyPart = secret.StartsWith("whsec_", StringComparison.Ordinal) ? secret["whsec_".Length..] : secret;
        byte[] key;
        try { key = Convert.FromBase64String(keyPart); }
        catch (FormatException) { key = Encoding.UTF8.GetBytes(keyPart); }

        using var hmac = new HMACSHA256(key);
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{svixId}.{svixTimestamp}.{body}")));
        var expectedBytes = Encoding.ASCII.GetBytes(expected);

        foreach (var part in svixSignature.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var comma = part.IndexOf(',');
            var sig = comma >= 0 ? part[(comma + 1)..] : part;
            var sigBytes = Encoding.ASCII.GetBytes(sig);
            if (sigBytes.Length == expectedBytes.Length &&
                CryptographicOperations.FixedTimeEquals(sigBytes, expectedBytes))
                return true;
        }
        return false;
    }
}
