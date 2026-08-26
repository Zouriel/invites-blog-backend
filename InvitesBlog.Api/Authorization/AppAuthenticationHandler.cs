using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Encodings.Web;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace InvitesBlog.Api.Authorization;

/// <summary>
/// Resolves every request into a permissioned <see cref="ClaimsPrincipal"/> for the full-RBAC model:
///   - a valid campaign possession token → <c>Inviter</c> role (+ campaign_id claim),
///   - a valid OTP-JWT → <c>Invitee</c> (or <c>Admin</c>) role (+ contact claims),
///   - anything else → the anonymous <c>Public</c> role.
/// Permission claims come from <see cref="Roles.Definitions"/>. Resource ownership (this token →
/// this campaign) is then enforced in the service layer; the policies gate the action type.
/// </summary>
public sealed class AppAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ICampaignRepository campaigns,
    InviteeJwt inviteeJwt)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "InvitesBlog";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        var token = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;

        ClaimsIdentity identity;

        if (string.IsNullOrEmpty(token))
        {
            identity = BuildIdentity(Roles.Public, authenticated: false);
        }
        else if (token.Count(c => c == '.') == 2 && TryReadJwt(token, out var jwtClaims))
        {
            // A token may carry SEVERAL roles — one person can be an admin, a designer and a
            // customer at once — so the identity holds the union of what they all grant.
            var roles = jwtClaims.Where(c => c.Type == ClaimTypes.Role)
                .Select(c => c.Value).Distinct(StringComparer.Ordinal).ToList();
            if (roles.Count == 0) roles.Add(Roles.Invitee);
            identity = BuildIdentity(roles, authenticated: true, extra: jwtClaims);
        }
        else
        {
            // A bearer token that isn't a JWT is one of two campaign secrets, either of which grants
            // Inviter for that one campaign (scoped by the campaign_id claim below, re-checked by
            // ICampaignOwnershipService on every campaign-scoped action): the possession token from
            // the builder, or the dashboard token from the emailed "Sent" link. Both prove the same
            // thing — this caller holds a secret only the campaign's owner has — so both should be
            // able to manage it (add/resend/cancel guests), not just read it.
            var hash = TokenService.Hash(token);
            var campaign = await campaigns.GetByAccessTokenHashAsync(hash)
                ?? await campaigns.GetByDashboardTokenHashAsync(hash);
            identity = campaign is not null
                ? BuildIdentity(Roles.Inviter, authenticated: true,
                    extra: [new Claim(AppClaims.CampaignId, campaign.Id.ToString())])
                : BuildIdentity(Roles.Public, authenticated: false);
        }

        var principal = new ClaimsPrincipal(identity);
        return AuthenticateResult.Success(new AuthenticationTicket(principal, SchemeName));
    }

    private static ClaimsIdentity BuildIdentity(string role, bool authenticated, IEnumerable<Claim>? extra = null) =>
        BuildIdentity([role], authenticated, extra);

    private static ClaimsIdentity BuildIdentity(
        IReadOnlyCollection<string> roles, bool authenticated, IEnumerable<Claim>? extra = null)
    {
        // Only give a real authentication type when a token actually authenticated the caller,
        // so anonymous Public callers report IsAuthenticated == false while still holding the
        // Public permission set that gates open endpoints.
        var identity = new ClaimsIdentity(authenticated ? SchemeName : null);

        // Roles that arrived on the token already; only the rest need synthesizing. Checking the
        // claims themselves rather than "was `extra` supplied" — an invitee JWT carries contact claims
        // but NO role, and a campaign possession token carries only campaign_id, so both used to end
        // up with the right PERMISSIONS but no role claim at all, leaving CurrentUser.Role reading
        // Public for every authenticated caller.
        var present = extra?.Where(c => c.Type == ClaimTypes.Role).Select(c => c.Value)
            .ToHashSet(StringComparer.Ordinal) ?? [];

        var granted = new HashSet<string>(StringComparer.Ordinal);
        foreach (var role in roles)
        {
            if (!present.Contains(role)) identity.AddClaim(new Claim(ClaimTypes.Role, role));
            if (Roles.Definitions.TryGetValue(role, out var perms)) granted.UnionWith(perms);
        }
        foreach (var p in granted) identity.AddClaim(new Claim(AppClaims.Permission, p));
        if (extra is not null) identity.AddClaims(extra);
        return identity;
    }

    private bool TryReadJwt(string token, out List<Claim> claims)
    {
        claims = [];
        try
        {
            var handler = new JwtSecurityTokenHandler();
            var principal = handler.ValidateToken(token, inviteeJwt.ValidationParameters(), out _);
            claims = principal.Claims.ToList();
            return true;
        }
        catch
        {
            return false;
        }
    }
}
