namespace InvitesBlog.Application.Abstractions;

/// <summary>
/// Sign in with Apple's token endpoints, for App Store guideline 5.1.1(v): deleting an account must
/// revoke the person's Apple tokens. A sign-in's one-time authorization code is exchanged for a refresh
/// token, kept sealed on the Apple login, and revoked when the account is deleted.
/// <para>
/// Needs a Sign in with Apple key (OAuth:Apple:KeyId / PrivateKey / TeamId). Without one
/// <see cref="IsConfigured"/> is false: sign-in still works, nothing is exchanged or revoked.
/// </para>
/// </summary>
public interface IAppleSignInTokens
{
    bool IsConfigured { get; }

    /// <summary>The refresh token for a sign-in's authorization code, or null when Apple refuses it or
    /// can't be reached. Never throws for Apple's answer: a sign-in must not fail over this.</summary>
    /// <param name="clientId">The client the code was issued to (the identity token's audience: the app's
    /// bundle id or the website's Services ID); null means the app's. One we don't own is refused.</param>
    Task<string?> ExchangeAsync(string authorizationCode, string? clientId = null, CancellationToken ct = default);

    /// <summary>Revokes a refresh token. Throws when Apple doesn't confirm it, so the caller retries.</summary>
    /// <param name="clientId">The client the token was issued to; null means the app's.</param>
    Task RevokeAsync(string refreshToken, string? clientId = null, CancellationToken ct = default);
}
