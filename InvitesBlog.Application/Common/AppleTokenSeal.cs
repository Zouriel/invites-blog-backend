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

    public static string Seal(string refreshToken, IConfiguration config) => TokenSeal.Seal(refreshToken, Secret(config), Purpose);

    public static string? Open(string? sealedText, IConfiguration config) => TokenSeal.Open(sealedText, Secret(config), Purpose);
}
