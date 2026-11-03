using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Common;

/// <summary>
/// Seals an Apple refresh token for the database (see <see cref="TokenSeal"/>): a copy of the database
/// alone can't be used to act as our app towards Apple. Keyed on the server's JWT signing secret.
/// </summary>
public static class AppleTokenSeal
{
    private const string Purpose = "apple-refresh-v1";

    private static string Secret(IConfiguration config) =>
        config["Jwt:SigningKey"] ?? "change-this-in-production-please-use-a-long-random-value";

    /// <param name="clientId">
    /// The client the token was issued to (the app's bundle id, or the website's Services ID). Apple only
    /// revokes a token for the client it was issued to, so it is kept with the token. Older seals have none,
    /// which means the iPhone app.
    /// </param>
    public static string Seal(string refreshToken, IConfiguration config, string? clientId = null) =>
        TokenSeal.Seal(string.IsNullOrWhiteSpace(clientId) ? refreshToken : $"{clientId}\n{refreshToken}", Secret(config), Purpose);

    /// <summary>The refresh token alone, or null when it can't be unsealed.</summary>
    public static string? Open(string? sealedText, IConfiguration config) => OpenWithClient(sealedText, config)?.Token;

    /// <summary>The refresh token and the client it was issued to (null: the iPhone app's).</summary>
    public static (string Token, string? ClientId)? OpenWithClient(string? sealedText, IConfiguration config)
    {
        var plain = TokenSeal.Open(sealedText, Secret(config), Purpose);
        if (plain is null) return null;
        var cut = plain.IndexOf('\n');
        return cut < 0 ? (plain, null) : (plain[(cut + 1)..], plain[..cut]);
    }
}
