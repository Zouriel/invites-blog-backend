using System.Security.Claims;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Admin;
using InvitesBlog.Application.Exceptions.Admin;
using InvitesBlog.Application.Filters.Admin;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Admin;

/// <summary>
/// Admin business logic (full-RBAC). Read-only inspection of users/roles/permissions plus paged
/// suppression + audit review, and password-verified admin login that issues a role-aware JWT.
/// </summary>
public sealed class AdminService(
    IRepository<AppUser> users,
    IRepository<Role> roles,
    IRepository<Permission> permissions,
    ISuppressionRepository suppression,
    IRepository<AuditLog> auditLogs,
    IInviteeTokenIssuer tokenIssuer) : IAdminService
{
    private static readonly TimeSpan AdminSessionLifetime = TimeSpan.FromHours(8);

    public async Task<PagedResult<AdminUserDto>> ListUsersAsync(AdminUserFilter filter, CancellationToken ct = default)
    {
        var query = users.Query();

        if (filter.IsActive is { } active)
            query = query.Where(u => u.IsActive == active);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(u => u.Email.ToLower().Contains(term) || u.DisplayName.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var page = await query
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .OrderBy(u => u.Email)
            .Skip(filter.Skip).Take(filter.PageSize)
            .ToListAsync(ct);

        var items = page.Select(u => new AdminUserDto(
            u.Id, u.Email, u.DisplayName, u.IsActive,
            u.UserRoles.Select(ur => ur.Role.Name).OrderBy(n => n).ToList())).ToList();

        return PagedResult<AdminUserDto>.Create(items, total, filter);
    }

    public async Task<IReadOnlyList<AdminRoleDto>> ListRolesAsync(CancellationToken ct = default)
    {
        var list = await roles.Query()
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        return list.Select(r => new AdminRoleDto(
            r.Id, r.Name, r.Description, r.IsSystem,
            r.RolePermissions.Select(rp => rp.Permission.Name).OrderBy(n => n).ToList())).ToList();
    }

    public async Task<IReadOnlyList<AdminPermissionDto>> ListPermissionsAsync(CancellationToken ct = default) =>
        await permissions.Query()
            .OrderBy(p => p.Group).ThenBy(p => p.Name)
            .Select(p => new AdminPermissionDto(p.Id, p.Name, p.Group, p.Description))
            .ToListAsync(ct);

    public async Task<PagedResult<SuppressionEntryDto>> ListSuppressionAsync(SuppressionFilter filter, CancellationToken ct = default)
    {
        var query = suppression.Query();

        if (!string.IsNullOrWhiteSpace(filter.ContactType))
            query = query.Where(s => s.ContactType == filter.ContactType);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip(filter.Skip).Take(filter.PageSize)
            .Select(s => new SuppressionEntryDto(s.Id, s.ContactHash, s.ContactType, s.CreatedAt))
            .ToListAsync(ct);

        return PagedResult<SuppressionEntryDto>.Create(items, total, filter);
    }

    public async Task<PagedResult<AuditLogDto>> ListAuditAsync(AuditLogFilter filter, CancellationToken ct = default)
    {
        var query = auditLogs.Query();

        if (!string.IsNullOrWhiteSpace(filter.Action))
            query = query.Where(a => a.Action == filter.Action);
        if (filter.CampaignId is { } campaignId)
            query = query.Where(a => a.CampaignId == campaignId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(a => a.Action.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip(filter.Skip).Take(filter.PageSize)
            .Select(a => new AuditLogDto(a.Id, a.Action, a.Actor, a.CampaignId, a.DataJson, a.CreatedAt))
            .ToListAsync(ct);

        return PagedResult<AuditLogDto>.Create(items, total, filter);
    }

    public async Task<AdminLoginResultDto> LoginAsync(AdminLoginRequest request, CancellationToken ct = default)
    {
        var email = (request.Email ?? string.Empty).Trim().ToLowerInvariant();

        var user = await users.Query()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Email == email, ct);

        // Uniform failure: never reveal whether the account exists.
        if (user is null || !user.IsActive ||
            string.IsNullOrEmpty(user.PasswordHash) ||
            !PasswordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
            throw new AdminLoginFailedException();

        var roleNames = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        if (!roleNames.Contains(Roles.Admin))
            throw new AdminLoginFailedException();

        var claims = new Dictionary<string, string>
        {
            [ClaimTypes.NameIdentifier] = user.Id.ToString(),
            ["email"] = user.Email
        };
        var token = tokenIssuer.IssueForRole(Roles.Admin, claims, AdminSessionLifetime);
        var expiresAt = DateTimeOffset.UtcNow.Add(AdminSessionLifetime);

        var userDto = new AdminUserDto(
            user.Id, user.Email, user.DisplayName, user.IsActive, roleNames.OrderBy(n => n).ToList());

        return new AdminLoginResultDto(token, expiresAt, userDto);
    }
}
