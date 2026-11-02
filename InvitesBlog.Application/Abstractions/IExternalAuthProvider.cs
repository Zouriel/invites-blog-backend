namespace InvitesBlog.Application.Abstractions;

/// <summary>
/// What the sign-in page needs to start the OAuth dance itself: the provider's authorization endpoint
/// and our public client id. No secret is involved — the client returns an ID token, which the server
/// then verifies against the provider's published keys.
/// </summary>
public sealed record ExternalAuthDescriptor(string Provider, string ClientId, string AuthorizeUrl);

/// <summary>The verified identity an OAuth provider asserted about a signer-in.</summary>
/// <param name="Provider">Lowercased provider key — <c>google</c> | <c>microsoft</c>.</param>
/// <param name="SubjectId">The provider's immutable id for this person. The linking key — never the email.</param>
/// <param name="Email">The verified email address, lowercased.</param>
/// <param name="DisplayName">Best available display name, falling back to the email's local part.</param>
/// <param name="PictureUrl">The provider's profile picture (Google's <c>picture</c> claim), https only, or null.</param>
/// <param name="EmailVerified">
/// True unless the provider leaves the email unconfirmed. Apple's token carries no email at all on some
/// sign-ins (then Email is empty) and must say <c>email_verified</c> before the email may link an
/// existing account; Google and Microsoft keep their old meaning (refused only when it says false).
/// </param>
public sealed record ExternalIdentity(
    string Provider, string SubjectId, string Email, string DisplayName, string? PictureUrl = null,
    bool EmailVerified = true);

/// <summary>
/// Verifies an ID token issued by an external identity provider (§designer OAuth sign-in). The client
/// runs the OAuth dance and posts us the resulting ID token; the implementation validates its signature
/// against the provider's published keys and confirms the audience is our own client id — so a token
/// minted for some other application can never sign anyone in here.
/// </summary>
public interface IExternalAuthProvider
{
    /// <summary>Lowercased provider key this implementation handles.</summary>
    string Provider { get; }

    /// <summary>
    /// True once the provider's client id is configured. When false the auth endpoints report
    /// <c>oauth_not_configured</c> rather than failing obscurely — email + password still works.
    /// </summary>
    bool IsConfigured { get; }

    /// <summary>What the sign-in page needs to start the dance. Throws when the provider isn't configured.</summary>
    ExternalAuthDescriptor Descriptor();

    /// <summary>
    /// True for a provider only the native apps can offer (Sign in with Apple: the website would need a
    /// Services ID it doesn't have). Listed in the sign-in options only when an app asks for its platform.
    /// </summary>
    bool AppOnly => false;

    /// <summary>
    /// Validates the ID token and returns the identity it asserts.
    /// Throws when the token is invalid, expired, for another audience, or carries an unverified email.
    /// <paramref name="rawNonce"/>, when given, must hash (SHA-256, hex) to the token's <c>nonce</c>: the
    /// app made it up and asked the provider to sign its hash, so a token lifted from elsewhere can't be
    /// replayed here. Providers that need one refuse a sign-in without it.
    /// </summary>
    Task<ExternalIdentity> VerifyAsync(string idToken, string? rawNonce = null, CancellationToken ct = default);
}
