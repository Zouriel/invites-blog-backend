using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Admin;
using InvitesBlog.Application.Filters.Admin;

namespace InvitesBlog.Application.Services.Admin;

/// <summary>Admin surface: user/role/permission inspection, suppression + audit review, admin login.</summary>
public interface IAdminService
{
    Task<PagedResult<AdminUserDto>> ListUsersAsync(AdminUserFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<AdminRoleDto>> ListRolesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AdminPermissionDto>> ListPermissionsAsync(CancellationToken ct = default);
    Task<PagedResult<SuppressionEntryDto>> ListSuppressionAsync(SuppressionFilter filter, CancellationToken ct = default);
    Task<PagedResult<AuditLogDto>> ListAuditAsync(AuditLogFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Gives one account a role, or takes it away, and returns the account as it now stands.
    ///
    /// <para>The only WRITE on this surface. Everything else here inspects; this is what makes a
    /// subscriber a subscriber, so it is also the one call that has to refuse things — see the
    /// implementation for which, and why each refusal exists.</para>
    /// </summary>
    Task<AdminUserDto> SetUserRoleAsync(
        Guid userId, SetUserRoleRequest req, CancellationToken ct = default);
}
