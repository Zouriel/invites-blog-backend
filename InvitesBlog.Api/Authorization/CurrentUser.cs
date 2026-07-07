using System.Security.Claims;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Domain.Authorization;

namespace InvitesBlog.Api.Authorization;

/// <summary>Reads the resolved principal's claims for the current HTTP request.</summary>
public sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated => Principal?.Identity?.IsAuthenticated ?? false;

    public string Role => Principal?.FindFirstValue(ClaimTypes.Role) ?? Roles.Public;

    public Guid? CampaignId =>
        Guid.TryParse(Principal?.FindFirstValue(AppClaims.CampaignId), out var id) ? id : null;

    public string? ContactType => Principal?.FindFirstValue(AppClaims.ContactType);
    public string? Contact => Principal?.FindFirstValue(AppClaims.Contact);

    public Guid? UserId =>
        Guid.TryParse(Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public bool HasPermission(string permission) =>
        Principal?.Claims.Any(c => c.Type == AppClaims.Permission && c.Value == permission) ?? false;
}
