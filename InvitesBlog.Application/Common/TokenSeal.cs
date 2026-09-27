using System.Security.Cryptography;
using System.Text;

namespace InvitesBlog.Application.Common;

/// <summary>
/// Encrypts a secret so the server can read it back, with a key derived from the server's own
/// signing secret (configuration, never the database). What it's for: a QR code's link, which the
/// host should be able to copy again later. Stored hashed, it couldn't be; stored in the clear, a copy
/// of the database would be a list of working links. Sealed, a stolen backup still yields nothing
/// without the server's configuration.
/// </summary>
public static class TokenSeal
{
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static string Seal(string plain, string secret, string purpose)
    {
        var key = Key(secret, purpose);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[TagSize];
        using (var aes = new AesGcm(key, TagSize)) aes.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String([.. nonce, .. tag, .. cipher]);
    }

    /// <summary>The secret, or null if it wasn't sealed with this key (a changed secret, or tampering).</summary>
    public static string? Open(string? sealedText, string secret, string purpose)
    {
        if (string.IsNullOrEmpty(sealedText)) return null;
        try
        {
            var all = Convert.FromBase64String(sealedText);
            if (all.Length < NonceSize + TagSize) return null;
            var nonce = all.AsSpan(0, NonceSize);
            var tag = all.AsSpan(NonceSize, TagSize);
            var cipher = all.AsSpan(NonceSize + TagSize);
            var plain = new byte[cipher.Length];
            using var aes = new AesGcm(Key(secret, purpose), TagSize);
            aes.Decrypt(nonce, cipher, tag, plain);
            return Encoding.UTF8.GetString(plain);
        }
        catch (Exception e) when (e is FormatException or CryptographicException)
        {
            return null;
        }
    }

    private static byte[] Key(string secret, string purpose) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, Encoding.UTF8.GetBytes(secret), 32,
            info: Encoding.UTF8.GetBytes("invites.blog/" + purpose));
}
