using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using Microsoft.Extensions.Configuration;
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
    private readonly SemaphoreSlim keyLock = new(1, 1);
    private IReadOnlyList<SecurityKey> cachedKeys = [];
    private DateTimeOffset keysFetchedAt = DateTimeOffset.MinValue;

    protected OpenIdConnectAuthProvider(IConfiguration config, IHttpClientFactory httpClientFactory)
    {
        this.httpClientFactory = httpClientFactory;
        ClientId = config[$"OAuth:{ConfigSection}:ClientId"];
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

    public bool IsConfigured => !string.IsNullOrWhiteSpace(ClientId);

    public ExternalAuthDescriptor Descriptor() =>
        IsConfigured
            ? new ExternalAuthDescriptor(Provider, ClientId!, AuthorizeUrl)
            : throw new BusinessRuleException(
                $"{Provider} sign-in isn't configured on this server.", "oauth_not_configured");

    public async Task<ExternalIdentity> VerifyAsync(string idToken, CancellationToken ct = default)
    {
        if (!IsConfigured)
            throw new BusinessRuleException(
                $"{Provider} sign-in isn't configured on this server yet — use email and password for now.",
                "oauth_not_configured");

        if (string.IsNullOrWhiteSpace(idToken))
            throw new UnauthorizedException("No sign-in token was supplied.", "oauth_invalid_token");

        var principal = await ValidateAsync(idToken, allowKeyRefresh: true, ct);

        var subject = principal.FindFirst("sub")?.Value;
        var email = principal.FindFirst("email")?.Value?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(email))
            throw new UnauthorizedException(
                $"{Provider} did not return an email address for this account.", "oauth_no_email");

        // An unverified email would let anyone claim someone else's account by signing up with it.
        var verified = principal.FindFirst("email_verified")?.Value;
        if (verified is not null && !string.Equals(verified, "true", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedException(
                $"Verify your email address with {Provider} before signing in here.", "oauth_email_unverified");

        var name = DisplayName(principal.Claims.ToDictionary(c => c.Type, c => c.Value, StringComparer.Ordinal));
        return new ExternalIdentity(Provider, subject, email,
            string.IsNullOrWhiteSpace(name) ? email.Split('@')[0] : name);
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
            ValidAudience = ClientId,
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
            throw new UnauthorizedException(
                $"That {Provider} sign-in couldn't be verified. Please try again.", "oauth_invalid_token");
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
public sealed class GoogleAuthProvider(IConfiguration config, IHttpClientFactory http)
    : OpenIdConnectAuthProvider(config, http)
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

    public MicrosoftAuthProvider(IConfiguration config, IHttpClientFactory http) : base(config, http)
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

    /// <summary>Personal Microsoft accounts often carry only <c>preferred_username</c>.</summary>
    protected override string? DisplayName(IReadOnlyDictionary<string, string> claims) =>
        claims.GetValueOrDefault("name") ?? claims.GetValueOrDefault("preferred_username");
}
