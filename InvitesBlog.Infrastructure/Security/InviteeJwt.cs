using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace InvitesBlog.Infrastructure.Security;

/// <summary>
/// Issues and validates the OTP-verified invitee JWT (§10.7 / §10.8). Lives in Infrastructure so the
/// Application OTP service can issue via <see cref="IInviteeTokenIssuer"/> and the API auth handler
/// can read the same validation parameters.
/// </summary>
public sealed class InviteeJwt(IConfiguration config) : IInviteeTokenIssuer
{
    public const string ContactTypeClaim = "contact_type"; // "phone" | "email"
    public const string ContactClaim = "contact";          // E.164 or lowercased email

    private string Key => config["Jwt:SigningKey"] ?? "change-this-in-production-please-use-a-long-random-value";
    private string Issuer => config["Jwt:Issuer"] ?? "invites.blog";
    private string Audience => config["Jwt:Audience"] ?? "invites.blog";

    public string Issue(string contactType, string contact, TimeSpan lifetime)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: Issuer,
            audience: Audience,
            claims:
            [
                new Claim(ContactTypeClaim, contactType),
                new Claim(ContactClaim, contact)
            ],
            expires: DateTime.UtcNow.Add(lifetime),
            signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public string IssueForRole(string role, IReadOnlyDictionary<string, string> claims, TimeSpan lifetime)
    {
        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key)), SecurityAlgorithms.HmacSha256);
        var claimList = new List<Claim> { new(ClaimTypes.Role, role) };
        claimList.AddRange(claims.Select(kv => new Claim(kv.Key, kv.Value)));
        var token = new JwtSecurityToken(
            issuer: Issuer, audience: Audience, claims: claimList,
            expires: DateTime.UtcNow.Add(lifetime), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    public TokenValidationParameters ValidationParameters() => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Key))
    };
}
