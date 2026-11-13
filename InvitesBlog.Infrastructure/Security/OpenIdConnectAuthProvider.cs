using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Exceptions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace InvitesBlog.Infrastructure.Security;

/// <summary>
/// Verifies an OpenID Connect ID token against a provider's published JWKS. Both Google and Microsoft
/// are plain OIDC, so they differ only in issuer/JWKS URL and the claim their display name lives in —
/// hence one implementation with two thin subclasses rather than two copies of the same validation.
/// <para>
/// Signing keys are fetched once and cached; a token signed with a key we haven't seen (the provider
/// rotated) triggers exactly one refetch, so rotation heals itself without restarting the API.
/// </para>
/// </summary>
public abstract class OpenIdConnectAuthProvider : IExternalAuthProvider
{
    private static readonly TimeSpan KeyCacheLifetime = TimeSpan.FromHours(12);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger? logger;
    private readonly SemaphoreSlim keyLock = new(1, 1);
    private IReadOnlyList<SecurityKey> cachedKeys = [];
    private DateTimeOffset keysFetchedAt = DateTimeOffset.MinValue;

    protected OpenIdConnectAuthProvider(IConfiguration config, IHttpClientFactory httpClientFactory, ILogger? logger = null)
    {
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
        ClientId = config[$"OAuth:{ConfigSection}:ClientId"];
        AdditionalClientIds = SplitIds(config[$"OAuth:{ConfigSection}:AdditionalClientIds"]);
    }

    /// <summary>For a token addressed to something other than a sign-in client.</summary>
    protected OpenIdConnectAuthProvider(string? audience, IHttpClientFactory httpClientFactory)
    {
        this.httpClientFactory = httpClientFactory;
        ClientId = audience;
    }

    public abstract string Provider { get; }
    /// <summary>The <c>OAuth:&lt;section&gt;:ClientId</c> configuration key this provider reads.</summary>
    protected abstract string ConfigSection { get; }
    /// <summary>Where the provider publishes its signing keys.</summary>
    protected abstract string JwksUri { get; }
    /// <summary>Every issuer value the provider may legitimately stamp on a token.</summary>
    protected abstract string[] ValidIssuers { get; }

    /// <summary>The provider's OIDC authorization endpoint the sign-in popup navigates to.</summary>
    protected abstract string AuthorizeUrl { get; }

    /// <summary>
    /// Overridden when the exact issuer can't be listed up front (Microsoft stamps a per-tenant issuer).
    /// Returns the accepted issuer, or throws to reject it.
    /// </summary>
    protected virtual IssuerValidator? IssuerValidator => null;

    protected string? ClientId { get; }

    /// <summary>
    /// Other clients of OUR app whose tokens are accepted too (<c>OAuth:&lt;section&gt;:AdditionalClientIds</c>,
    /// comma-separated). A native iPhone sign-in returns an ID token addressed to the iOS client id, not
    /// the web one, so without this every iPhone sign-in is refused as "minted for a different client".
    /// Never handed to clients: the descriptor still gives the web client id.
    /// </summary>
    protected IReadOnlyList<string> AdditionalClientIds { get; } = [];

    /// <summary>Further audiences a subclass accepts while some switch is on (Apple's website client).</summary>
    protected virtual IEnumerable<string> SwitchedAudiences => [];

    private static IReadOnlyList<string> SplitIds(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? []
            : raw.Split([',', ';', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public ExternalAuthDescriptor Descriptor() =>
        IsConfigured
            ? new ExternalAuthDescriptor(Provider, ClientId!, AuthorizeUrl)
            : throw new BusinessRuleException(
                $"{Provider} sign-in isn't configured on this server.", "oauth_not_configured");

    /// <summary>True when a sign-in must carry the raw nonce whose hash is in the token (Apple).</summary>
    protected virtual bool RequiresNonce => false;

    /// <summary>
    /// False when a token may carry no email (Apple sends it reliably on the first sign-in only); the
    /// identity then has an empty email and only an existing link can sign it in.
    /// </summary>
    protected virtual bool RequiresEmail => true;

    /// <summary>
    /// What an absent <c>email_verified</c> means. Google: verified (unchanged). Apple always states it, so
    /// absent means not verified; Microsoft never does (see its override): neither links an account by email then.
    /// </summary>
    protected virtual bool EmailVerifiedWhenUnstated => true;

    public virtual bool AppOnly => false;

    /// <summary>Declared here (not only as the interface's default) so a subclass can override it: a
    /// default interface method would be what callers get, whatever a subclass declares.</summary>
    public virtual ExternalAuthDescriptor? DescriptorFor(string? platform) =>
        !IsConfigured || (AppOnly && !string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase))
            ? null
            : Descriptor();

    public async Task<ExternalIdentity> VerifyAsync(string idToken, string? rawNonce = null, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new BusinessRuleException(
                $"{Provider} sign-in isn't configured on this server yet — use email and password for now.",
                "oauth_not_configured");

        if (string.IsNullOrWhiteSpace(idToken))
            throw new UnauthorizedException("No sign-in token was supplied.", "oauth_invalid_token");

        if (RequiresNonce && string.IsNullOrEmpty(rawNonce))
        {
            logger?.LogWarning("{Provider} sign-in token refused (no nonce sent)", Provider);
            throw new UnauthorizedException(
                $"That {Provider} sign-in couldn't be verified. Please try again.", "oauth_invalid_token");
        }

        var principal = await ValidateAsync(idToken, allowKeyRefresh: true, ct);

        if (!string.IsNullOrEmpty(rawNonce) && !NonceMatches(rawNonce, principal.FindFirst("nonce")?.Value))
        {
            logger?.LogWarning("{Provider} sign-in token refused (nonce mismatch) for client {ClientId}",
                Provider, principal.FindFirst("aud")?.Value);
            throw new UnauthorizedException(
                $"That {Provider} sign-in couldn't be verified. Please try again.", "oauth_invalid_token");
        }
        // Which of our clients the token was for (web, or the iPhone app's own): how a first sign-in
        // from a new platform can be confirmed from the log. No email, subject or token is logged.
        logger?.LogInformation("{Provider} sign-in token accepted for client {ClientId}",
            Provider, principal.FindFirst("aud")?.Value);

        var subject = principal.FindFirst("sub")?.Value;
        var email = principal.FindFirst("email")?.Value?.Trim().ToLowerInvariant() ?? "";

        if (string.IsNullOrWhiteSpace(subject) || (RequiresEmail && string.IsNullOrWhiteSpace(email)))
            throw new UnauthorizedException(
                $"{Provider} did not return an email address for this account.", "oauth_no_email");

        // An unverified email would let anyone claim someone else's account by signing up with it.
        var verified = principal.FindFirst("email_verified")?.Value;
        if (verified is not null && !string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedException(
                $"Verify your email address with {Provider} before signing in here.", "oauth_email_unverified");
        var emailVerified = email.Length > 0 && (verified is null ? EmailVerifiedWhenUnstated : true);

        var name = DisplayName(principal.Claims.ToDictionary(c => c.Type, c => c.Value, StringComparer.Ordinal));
        // Google puts a picture in the ID token when the `profile` scope was asked for; Microsoft's
        // tokens carry none. Only an https URL is passed on.
        var picture = principal.FindFirst("picture")?.Value;
        if (!Uri.TryCreate(picture, UriKind.Absolute, out var pictureUri) || pictureUri.Scheme != Uri.UriSchemeHttps)
            picture = null;
        return new ExternalIdentity(Provider, subject, email,
            string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name, picture, emailVerified,
            principal.FindFirst("aud")?.Value);
    }

    /// <summary>The token's nonce must be the hex SHA-256 of the raw nonce the app kept.</summary>
    private static bool NonceMatches(string rawNonce, string? tokenNonce)
    {
        if (string.IsNullOrEmpty(tokenNonce)) return false;
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawNonce)));
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(tokenNonce.ToLowerInvariant()));
    }

    /// <summary>The provider's best display-name claim; null falls back to the email's local part.</summary>
    protected virtual string? DisplayName(IReadOnlyDictionary<string, string> claims) =>
        claims.GetValueOrDefault("name");

    private async Task<System.Security.Claims.ClaimsPrincipal> ValidateAsync(
        string idToken, bool allowKeyRefresh, CancellationToken ct)
    {
        // Check the SHAPE before fetching anything. Otherwise a caller posting rubbish makes this
        // server call out to the provider for signing keys it will never use.
        // MapInboundClaims is ON by default and rewrites OIDC claim names to the old WS-Federation
        // URIs — `sub` becomes .../nameidentifier, `email` becomes .../emailaddress. Everything below
        // reads the OIDC names, so leaving it on makes a perfectly good token look like one carrying
        // no subject and no email.
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        if (!handler.CanReadToken(idToken))
            throw new UnauthorizedException(
                $"That {Provider} sign-in couldn't be verified. Please try again.", "oauth_invalid_token");

        var keys = await SigningKeysAsync(force: false, ct);
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuers = ValidIssuers,
            ValidateAudience = true,
            ValidAudiences = [ClientId!, .. AdditionalClientIds, .. SwitchedAudiences],
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKeys = keys
        };
        if (IssuerValidator is { } issuerValidator) parameters.IssuerValidator = issuerValidator;

        try
        {
            return handler.ValidateToken(idToken, parameters, out _);
        }
        catch (SecurityTokenSignatureKeyNotFoundException) when (allowKeyRefresh)
        {
            // The provider rotated its signing keys — refetch once and retry before giving up.
            await SigningKeysAsync(force: true, ct);
            return await ValidateAsync(idToken, allowKeyRefresh: false, ct);
        }
        // A malformed token is an ARGUMENT exception, not a SecurityTokenException — anything that
        // isn't three dot-separated segments never reaches signature validation. Both are the caller
        // sending us something unusable, so both are a 401 rather than a 500 with a stack trace.
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            logger?.LogWarning("{Provider} sign-in token refused ({Reason}) for client {ClientId}",
                Provider, ex.GetType().Name, UnverifiedAudience(handler, idToken));
            throw new UnauthorizedException(
                $"That {Provider} sign-in couldn't be verified. Please try again.", "oauth_invalid_token");
        }
    }

    /// <summary>The token's own (unverified) audience, for the refusal log only: shortened and printable.</summary>
    private static string UnverifiedAudience(JwtSecurityTokenHandler handler, string idToken)
    {
        try
        {
            var aud = string.Join(",", handler.ReadJwtToken(idToken).Audiences);
            var clean = new string(aud.Where(c => !char.IsControl(c)).Take(120).ToArray());
            return clean.Length == 0 ? "(none)" : clean;
        }
        catch (Exception)
        {
            return "(unreadable)";
        }
    }

    private async Task<IReadOnlyList<SecurityKey>> SigningKeysAsync(bool force, CancellationToken ct)
    {
        if (!force && cachedKeys.Count > 0 && DateTimeOffset.UtcNow - keysFetchedAt < KeyCacheLifetime)
            return cachedKeys;

        await keyLock.WaitAsync(ct);
        try
        {
            if (!force && cachedKeys.Count > 0 && DateTimeOffset.UtcNow - keysFetchedAt < KeyCacheLifetime)
                return cachedKeys;

            using var http = httpClientFactory.CreateClient();
            var json = await http.GetStringAsync(JwksUri, ct);
            cachedKeys = new JsonWebKeySet(json).GetSigningKeys().ToList();
            keysFetchedAt = DateTimeOffset.UtcNow;
            return cachedKeys;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or ArgumentException)
        {
            throw new BusinessRuleException(
                $"Couldn't reach {Provider} to verify the sign-in. Please try again in a moment.",
                "oauth_provider_unreachable");
        }
        finally
        {
            keyLock.Release();
        }
    }
}

/// <summary>Google sign-in (<c>OAuth:Google:ClientId</c>).</summary>
public sealed class GoogleAuthProvider(IConfiguration config, IHttpClientFactory http, ILogger<GoogleAuthProvider>? logger = null)
    : OpenIdConnectAuthProvider(config, http, logger)
{
    public const string Key = "google";

    public override string Provider => Key;
    protected override string ConfigSection => "Google";
    protected override string JwksUri => "https://www.googleapis.com/oauth2/v3/certs";
    protected override string[] ValidIssuers => ["https://accounts.google.com", "accounts.google.com"];
    protected override string AuthorizeUrl => "https://accounts.google.com/o/oauth2/v2/auth";
}

/// <summary>
/// Microsoft sign-in (<c>OAuth:Microsoft:ClientId</c>). Multi-tenant tokens carry a per-tenant issuer,
/// so the tenant the app is registered against is configurable (<c>OAuth:Microsoft:Tenant</c>,
/// default <c>common</c> — which accepts any tenant plus personal accounts).
/// </summary>
public sealed class MicrosoftAuthProvider : OpenIdConnectAuthProvider
{
    public const string Key = "microsoft";

    private readonly string tenant;

    public MicrosoftAuthProvider(IConfiguration config, IHttpClientFactory http, ILogger<MicrosoftAuthProvider>? logger = null)
        : base(config, http, logger)
    {
        tenant = config["OAuth:Microsoft:Tenant"] is { Length: > 0 } t ? t : "common";
    }

    public override string Provider => Key;
    protected override string ConfigSection => "Microsoft";
    protected override string JwksUri => $"https://login.microsoftonline.com/{tenant}/discovery/v2.0/keys";
    protected override string AuthorizeUrl => $"https://login.microsoftonline.com/{tenant}/oauth2/v2.0/authorize";

    // A v2.0 token's issuer names the TENANT the signer-in belongs to, which for "common" isn't known
    // ahead of time — so the issuer is matched by shape instead of by an exact string.
    protected override string[] ValidIssuers => [$"https://login.microsoftonline.com/{tenant}/v2.0"];

    protected override IssuerValidator? IssuerValidator =>
        tenant is "common" or "organizations" or "consumers" ? ValidateTenantIssuer : null;

    private static string ValidateTenantIssuer(
        string issuer, SecurityToken token, TokenValidationParameters parameters)
    {
        // https://login.microsoftonline.com/<tenant guid>/v2.0 — the tenant id must also be the token's
        // own tid claim, so a token can't claim to come from a tenant it wasn't issued for.
        const string prefix = "https://login.microsoftonline.com/";
        const string suffix = "/v2.0";
        var tid = (token as JwtSecurityToken)?.Claims.FirstOrDefault(c => c.Type == "tid")?.Value;

        if (tid is { Length: > 0 } && issuer == $"{prefix}{tid}{suffix}" && Guid.TryParse(tid, out _))
            return issuer;

        throw new SecurityTokenInvalidIssuerException($"Unexpected Microsoft issuer '{issuer}'.");
    }

    /// <summary>
    /// Microsoft's tokens never say <c>email_verified</c>, and in a work or school tenant the <c>email</c> claim is
    /// whatever that tenant's admin typed in, so it is never taken as proof of an address: a Microsoft sign-in
    /// links to an existing account only through its own link, never by matching the email.
    /// </summary>
    protected override bool EmailVerifiedWhenUnstated => false;

    /// <summary>Personal Microsoft accounts often carry only <c>preferred_username</c>.</summary>
    protected override string? DisplayName(IReadOnlyDictionary<string, string> claims) =>
        claims.GetValueOrDefault("name") ?? claims.GetValueOrDefault("preferred_username");
}

/// <summary>
/// Sign in with Apple from the iPhone app (<c>OAuth:Apple:ClientId</c>, the app's bundle id
/// blog.invites.app, which is the audience of a native sign-in's identity token).
/// <list type="bullet">
/// <item>The app must send the raw nonce; the token's <c>nonce</c> is its SHA-256.</item>
/// <item><c>sub</c> is the stable id. The email may be a private relay address and may be missing on
/// later sign-ins, so only an existing link signs in a token without one.</item>
/// <item>The website signs in through its own Services ID (<c>OAuth:Apple:WebClientId</c>,
/// blog.invites.web), accepted as an audience and listed in the website's options only while
/// <c>OAuth:Apple:WebEnabled</c> is on (<see cref="AppleWebSettings"/>). Android never lists Apple.</item>
/// </list>
/// <c>OAuth:Apple:BaseUrl</c> replaces https://appleid.apple.com for the keys (tests use a stand-in);
/// the issuer is checked against Apple's real one either way.
/// </summary>
public sealed class AppleAuthProvider : OpenIdConnectAuthProvider
{
    public const string Key = "apple";
    public const string Issuer = "https://appleid.apple.com";

    private readonly string baseUrl;
    private readonly AppleWebSettings web;

    public AppleAuthProvider(IConfiguration config, IHttpClientFactory http, ILogger<AppleAuthProvider>? logger = null)
        : base(config, http, logger)
    {
        baseUrl = AppleBaseUrl(config);
        web = AppleWebSettings.From(config);
    }

    /// <summary>Apple's sign-in host, or the stand-in tests point it at. No trailing slash.</summary>
    public static string AppleBaseUrl(IConfiguration config) =>
        (config["OAuth:Apple:BaseUrl"] is { Length: > 0 } b ? b : Issuer).TrimEnd('/');

    public override string Provider => Key;
    protected override string ConfigSection => "Apple";
    protected override string JwksUri => $"{baseUrl}/auth/keys";
    protected override string[] ValidIssuers => [Issuer];
    protected override string AuthorizeUrl => $"{Issuer}/auth/authorize";
    protected override bool RequiresNonce => true;
    protected override bool RequiresEmail => false;
    protected override bool EmailVerifiedWhenUnstated => false;
    public override bool AppOnly => true;

    protected override IEnumerable<string> SwitchedAudiences => web.IsOn ? [web.ClientId!] : [];

    /// <summary>
    /// The iPhone: the app's own client id. The website (no platform, or <c>web</c>): the Services ID and
    /// its return URL, only while web sign-in is on. Anything else (Android): not offered.
    /// </summary>
    public override ExternalAuthDescriptor? DescriptorFor(string? platform)
    {
        if (!IsConfigured) return null;
        if (string.Equals(platform, "ios", StringComparison.OrdinalIgnoreCase)) return Descriptor();
        var website = string.IsNullOrEmpty(platform) || string.Equals(platform, "web", StringComparison.OrdinalIgnoreCase);
        return website && web.IsOn
            ? new ExternalAuthDescriptor(Provider, web.ClientId!, AuthorizeUrl, web.RedirectUri)
            : null;
    }

    /// <summary>Apple's identity token carries no name; the app sends it on the first sign-in.</summary>
    protected override string? DisplayName(IReadOnlyDictionary<string, string> claims) => null;
}
