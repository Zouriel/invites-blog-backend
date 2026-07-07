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
    Task<AdminLoginResultDto> LoginAsync(AdminLoginRequest request, CancellationToken ct = default);
}
