namespace InvitesBlog.Application.Dtos.Accounts;

/// <summary>Email + password sign-in — the tab staff and designers use.</summary>
public sealed record PasswordLoginRequest(string Email, string Password);

/// <summary>
/// Asks for a sign-in code. One field: a phone number or an email address, whichever they typed.
/// </summary>
public sealed record RequestCodeRequest(string Identifier, string? DefaultCountry = null);

/// <summary>Where a code was sent, so the next screen can say so without echoing the full address.</summary>
/// <param name="Channel">"sms" or "email".</param>
/// <param name="SentTo">Masked destination, e.g. <c>+960 77• ••34</c> or <c>a•••a@example.com</c>.</param>
public sealed record CodeSentResponse(
    Guid ChallengeId, string Channel, string SentTo, int ExpiresInSeconds);

public sealed record VerifyCodeRequest(Guid ChallengeId, string Code);

/// <summary>The signed-in account: who they are, what they can do, and how they can be reached.</summary>
public sealed record AccountDto(
    Guid Id,
    string? Email,
    string? PhoneE164,
    string DisplayName,
    bool IsActive,
    bool HasPassword,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> LinkedProviders,
    /// <summary>"light" or "dark", or null to take the default. Follows the account, not the browser.</summary>
    string? ThemePreference = null);

/// <summary>Sets the account's light/dark preference. Anything but "dark" is treated as light.</summary>
public sealed record SetThemeRequest(string? Theme);

/// <summary>A successful sign-in: the session token plus the account it belongs to.</summary>
public sealed record AuthResultDto(string Token, DateTimeOffset ExpiresAt, AccountDto Account);

/// <summary>
/// The outcome of linking a second identifier. <paramref name="Merged"/> is true when another
/// account already existed for it and was absorbed into this one.
/// <para>
/// A fresh <paramref name="Token"/> comes back because a merge can GRANT ROLES: the token in hand
/// was minted before them, so continuing with it would leave the person newly entitled to screens
/// their session still can't open.
/// </para>
/// </summary>
public sealed record LinkResultDto(
    AccountDto Account, bool Merged, string? MergeSummary, string Token, DateTimeOffset ExpiresAt);

/// <summary>What the sign-in page needs to render itself honestly.</summary>
/// <param name="SmsAvailable">False when no SMS provider is configured, so the phone tab can say so.</param>
/// <param name="OAuthProviders">
/// Everything the browser needs to start the OAuth dance itself — provider key, our public client id
/// and the provider's authorize endpoint. Empty when no provider is configured, and the page then
/// shows no buttons rather than buttons that cannot work.
/// </param>
public sealed record AuthOptionsDto(bool SmsAvailable, IReadOnlyList<OAuthProviderDto> OAuthProviders);

/// <summary>One OAuth button the sign-in page can offer.</summary>
public sealed record OAuthProviderDto(string Provider, string ClientId, string AuthorizeUrl);

/// <summary>
/// Signing in with an ID token the browser obtained from the provider. The token is verified
/// server-side against the provider's published keys, so nothing here is trusted as sent.
/// </summary>
public sealed record OAuthLoginRequest(string IdToken);

/// <summary>
/// Creating a designer account: someone who wants to publish templates rather than only receive
/// invitations. Everyone else gets an account automatically the first time they sign in with a code.
/// </summary>
public sealed record RegisterDesignerRequest(string Email, string Password, string DisplayName);

/// <summary>
/// One invitation the signed-in customer created, for their history.
/// <para><c>PreviewImageUrl</c> is the template's own preview — what the invitation LOOKS like, which
/// is how a person picks theirs out of a grid of them.</para>
/// </summary>
public sealed record MyCampaignDto(
    Guid Id, string Title, string Slug, string Status, string EventType,
    DateTimeOffset EventStartAt, int GuestCount, string? TemplateName, DateTimeOffset CreatedAt,
    string? PreviewImageUrl = null, int PhotoCount = 0);

/// <summary>One bespoke-template request the signed-in customer made.</summary>
public sealed record MyRequestDto(
    Guid Id, string Occasion, string Message, bool HasAttended, bool TemplateIssued,
    Guid? IssuedTemplateId, string? IssuedTemplateSlug, DateTimeOffset CreatedAt);
