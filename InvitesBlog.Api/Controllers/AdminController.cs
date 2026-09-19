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

    /// <summary>
    /// Grant or revoke one role on one account — the only write here.
    ///
    /// <para>PUT rather than POST because it states what should be true afterwards: sending the same
    /// request twice leaves the same result, so a toggle that double-fires cannot half-apply.</para>
    /// </summary>
    [HttpPut("users/{id:guid}/roles")]
    [HasPermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> SetRole(Guid id, [FromBody] SetUserRoleRequest req, CancellationToken ct) =>
        Success(await admin.SetUserRoleAsync(id, req, ct));

    /// <summary>Sets an account's Studio or Venue plan. Until billing exists, this is how anyone gets one.</summary>
    [HttpPut("users/{id:guid}/subscription")]
    [HasPermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> SetSubscription(Guid id, [FromBody] SetSubscriptionRequest req, CancellationToken ct) =>
        Success(await admin.SetSubscriptionAsync(id, req, ct));

    [HttpGet("users/{id:guid}/events")]
    [HasPermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> UserEvents(Guid id, CancellationToken ct) =>
        Success(await admin.UserEventsAsync(id, ct));

    [HttpPut("events/{id:guid}/pass")]
    [HasPermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> SetEventPass(Guid id, [FromBody] SetEventPassRequest req, CancellationToken ct) =>
        Success(await admin.SetEventPassAsync(id, req, ct));

    [HttpPut("events/{id:guid}/keep-photos")]
    [HasPermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> KeepPhotos(Guid id, [FromBody] KeepPhotosRequest req, CancellationToken ct) =>
        Success(await admin.KeepPhotosAsync(id, req, ct));

    /// <summary>Adds passes to a Studio account's stock, or takes unused ones away.</summary>
    [HttpPost("users/{id:guid}/pass-credits")]
    [HasPermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> AdjustPassCredits(Guid id, [FromBody] AdjustPassCreditsRequest req, CancellationToken ct) =>
        Success(await admin.AdjustPassCreditsAsync(id, req, ct));

    [HttpGet("audit")]
    [HasPermission(Permissions.Admin.ReadAudit)]
    public async Task<IActionResult> Audit([FromQuery] AuditLogFilter filter, CancellationToken ct) =>
        Paged(await admin.ListAuditAsync(filter, ct));
}
