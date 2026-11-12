namespace InvitesBlog.Application.Dtos.Accounts;

/// <summary>Email + password sign-in — the tab staff and designers use.</summary>
public sealed record PasswordLoginRequest(string Email, string Password);

/// <summary>
/// Asks for a sign-in code. One field: a phone number or an email address, whichever they typed.
/// </summary>
/// <param name="DefaultCountry">ISO2 country of a phone typed without its country code (older name).</param>
/// <param name="PhoneCountry">The same; wins over DefaultCountry. Neither: the request's country (CF-IPCountry).</param>
public sealed record RequestCodeRequest(string Identifier, string? DefaultCountry = null, string? PhoneCountry = null);

/// <summary>Where a code was sent, so the next screen can say so without echoing the full address.</summary>
/// <param name="Channel">"sms" or "email".</param>
/// <param name="SentTo">Masked destination, e.g. <c>+447•••23</c> or <c>a•••a@example.com</c>.</param>
public sealed record CodeSentResponse(
    Guid ChallengeId, string Channel, string SentTo, int ExpiresInSeconds);

public sealed record VerifyCodeRequest(Guid ChallengeId, string Code);

/// <summary>
/// Finishing a customer sign-up: the code proves the address is theirs, and the password is what
/// they will actually sign in with afterwards.
/// </summary>
public sealed record SignUpRequest(
    Guid ChallengeId, string Code, string Password, string? DisplayName = null);

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
    string? ThemePreference = null,
    /// <summary>The account's plan in force right now: None, Premium or Venue (shown as "Studio").</summary>
    string SubscriptionTier = "None",
    DateTimeOffset? SubscriptionEndsAt = null,
    /// <summary>Always false: there is no venue page any more. Kept so app builds that read it keep working.</summary>
    bool AtVenue = false,
    /// <summary>The profile picture, or null to show initials.</summary>
    string? AvatarUrl = null,
    /// <summary>
    /// ISO2 country ("GB"): the default for phone numbers typed without a country code. Set from the
    /// request's country (Cloudflare) at the first sign-in; null when never known.
    /// </summary>
    string? Country = null,
    /// <summary>IANA time zone ("Europe/London"), the default for new events; null until the person sets one.</summary>
    string? TimeZone = null,
    /// <summary>Kinds of phone notification turned off in the app: some of "replies", "comments", "photos", "hearts", "reminders".</summary>
    IReadOnlyList<string>? MutedNotifications = null);

/// <summary>
/// The kinds of phone notification to turn off (the rest are on): any of "replies", "comments", "photos",
/// "hearts", "reminders". Anything else is ignored.
/// </summary>
public sealed record SetNotificationsRequest(IReadOnlyList<string>? Muted);

/// <summary>
/// The account's region settings. A field left out (null) is left as it is; an empty string clears it.
/// </summary>
/// <param name="Country">ISO 3166-1 alpha-2, one libphonenumber knows ("GB", "US", "MV").</param>
/// <param name="TimeZone">An IANA id ("Europe/London").</param>
public sealed record UpdateAccountSettingsRequest(string? Country = null, string? TimeZone = null);

/// <summary>The visitor's country as the request reports it (Cloudflare), or null when unknown.</summary>
public sealed record GeoDto(string? Country);

/// <summary>The account's new name, as the person typed it. See IAccountService.SetNameAsync for the rules.</summary>
public sealed record SetNameRequest(string? DisplayName);

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
/// <param name="RedirectUri">The return URL the provider must send the person back to, when it is fixed (Apple on the web).</param>
public sealed record OAuthProviderDto(string Provider, string ClientId, string AuthorizeUrl, string? RedirectUri = null);

/// <summary>
/// Signing in with an ID token the browser obtained from the provider. The token is verified
/// server-side against the provider's published keys, so nothing here is trusted as sent.
/// </summary>
/// <param name="IdToken">The provider's ID token.</param>
/// <param name="Nonce">The raw nonce the app made up and sent hashed to the provider (Apple: required).</param>
/// <param name="AuthorizationCode">
/// Apple's one-time code, exchanged for the refresh token that account deletion revokes. Optional; never
/// stored or logged as sent.
/// </param>
/// <param name="GivenName">The name Apple hands the app on the first sign-in only; used for a new account.</param>
/// <param name="FamilyName">See <paramref name="GivenName"/>.</param>
public sealed record OAuthLoginRequest(
    string IdToken, string? Nonce = null, string? AuthorizationCode = null,
    string? GivenName = null, string? FamilyName = null);

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
    string? PreviewImageUrl = null, int PhotoCount = 0,
    /// <summary>
    /// True for an event with a media bucket and no invitation. A campaign may be an invitation, a
    /// bucket, or both, and the list has to be able to say which — otherwise a bucket somebody
    /// bought for a trip is indistinguishable from an invitation they never finished.
    /// </summary>
    bool MediaOnly = false,
    /// <summary>The wizard step to continue from when the invitation isn't finished; null when it is.</summary>
    string? ResumeStep = null,
    /// <summary>"host" for the organiser's own events, "celebrant" for an event this person is for.</summary>
    string Relation = "host",
    /// <summary>For a celebrant: whether the organiser gave them full access.</summary>
    bool CanManage = true,
    /// <summary>What the event is on, for its badge: Free, GoldPass, PlatinumPass or Venue (its organiser's Studio plan; shown as "Studio").</summary>
    string Plan = "Free",
    /// <summary>"invitation" or "saveTheDate".</summary>
    string Kind = "invitation",
    /// <summary>The design's background and accent (#rrggbb), for a tile with no picture; null when it declares none.</summary>
    IReadOnlyList<string>? Colours = null,
    /// <summary>The event's IANA time zone: EventStartAt (an instant) is shown on this clock.</summary>
    string TimeZone = InvitesBlog.Domain.Events.EventZones.Legacy,
    /// <summary>The organiser's display name while their Studio plan (tier Venue) covers the event, for the small badge on its tile; null otherwise.</summary>
    string? VenueName = null,
    /// <summary>The organiser's profile picture for that badge; null when they have none (show the first letter) or there is no badge.</summary>
    string? VenueLogoUrl = null);

/// <summary>One bespoke-template request the signed-in customer made.</summary>
public sealed record MyRequestDto(Guid Id, string Occasion, string Message, bool HasAttended, DateTimeOffset CreatedAt);
