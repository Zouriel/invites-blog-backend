using System.Security.Cryptography;
using System.Text;

namespace InvitesBlog.Application.Security;

/// <summary>
/// Generates and hashes possession tokens (§9.3): invite tokens, campaign access tokens,
/// dashboard links. Raw tokens are 256-bit random, URL-safe, and NEVER stored — only the
/// SHA-256 hash lands in the database. On use: read token → hash → match → authorize.
/// </summary>
public static class TokenService
{
    public const int TokenBytes = 32; // 256-bit

    /// <summary>A new URL-safe 256-bit token. Return this to the client exactly once.</summary>
    public static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(TokenBytes);
        return Base64Url(bytes);
    }

    /// <summary>Lowercase-hex SHA-256 of the token, safe to persist and index.</summary>
    public static string Hash(string token)
    {
        ArgumentException.ThrowIfNullOrEmpty(token);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(token));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>Constant-time comparison of a presented token against a stored hash.</summary>
    public static bool Verify(string presentedToken, string storedHash)
    {
        if (string.IsNullOrEmpty(presentedToken) || string.IsNullOrEmpty(storedHash))
            return false;
        var computed = Hash(presentedToken);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(computed),
            Encoding.ASCII.GetBytes(storedHash));
    }

    /// <summary>Hash a contact (E.164 phone or lowercased email) for the suppression list (§15.3).</summary>
    public static string HashContact(string contact)
    {
        ArgumentException.ThrowIfNullOrEmpty(contact);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(contact.Trim().ToLowerInvariant()));
        return Convert.ToHexStringLower(hash);
    }

    /// <summary>A numeric OTP code of the given length (default 6, §11.1).</summary>
    public static string GenerateNumericCode(int digits = 6)
    {
        var sb = new StringBuilder(digits);
        for (var i = 0; i < digits; i++)
            sb.Append(RandomNumberGenerator.GetInt32(0, 10));
        return sb.ToString();
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
