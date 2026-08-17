using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Admin;
using InvitesBlog.Application.Filters.Admin;
using InvitesBlog.Application.Services.Admin;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>Admin surface (full-RBAC). Signing in happens at <c>/api/auth/login</c> like everyone else.</summary>
[Route("api/admin")]
public sealed class AdminController(IAdminService admin) : BaseApiController
{
    [HttpGet("users")]
    [HasPermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> Users([FromQuery] AdminUserFilter filter, CancellationToken ct) =>
        Paged(await admin.ListUsersAsync(filter, ct));

    [HttpGet("roles")]
    [HasPermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> ListRoles(CancellationToken ct) =>
        Success(await admin.ListRolesAsync(ct));

    [HttpGet("permissions")]
    [HasPermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> ListPermissions(CancellationToken ct) =>
        Success(await admin.ListPermissionsAsync(ct));

    [HttpGet("suppression")]
    [HasPermission(Permissions.Admin.ManageSuppression)]
    public async Task<IActionResult> Suppression([FromQuery] SuppressionFilter filter, CancellationToken ct) =>
        Paged(await admin.ListSuppressionAsync(filter, ct));

    [HttpGet("audit")]
    [HasPermission(Permissions.Admin.ReadAudit)]
    public async Task<IActionResult> Audit([FromQuery] AuditLogFilter filter, CancellationToken ct) =>
        Paged(await admin.ListAuditAsync(filter, ct));
}
