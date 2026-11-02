using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Moderation;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// Reporting what someone else put on invites.blog, and blocking people (guideline 1.2). Signed in
/// only: the signed-in account is who reports and who blocks. See docs/MODERATION.md.
/// </summary>
[Route("api/me")]
public sealed class ModerationController(IModerationService moderation) : BaseApiController
{
    [HttpPost("reports")]
    [HasPermission(Permissions.Inbox.Read)]
    [EnableRateLimiting("content-report")]
    public async Task<IActionResult> Report([FromBody] ReportContentRequest request, CancellationToken ct) =>
        Success(await moderation.ReportAsync(request, ct));

    [HttpGet("blocks")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> Blocked(CancellationToken ct) => Success(await moderation.BlockedAsync(ct));

    [HttpPut("blocks/{userId:guid}")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> Block(Guid userId, CancellationToken ct)
    {
        await moderation.BlockAsync(userId, ct);
        return SuccessMessage("Blocked. You won't see their posts, comments or photos.");
    }

    [HttpDelete("blocks/{userId:guid}")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> Unblock(Guid userId, CancellationToken ct)
    {
        await moderation.UnblockAsync(userId, ct);
        return SuccessMessage("Unblocked.");
    }
}

/// <summary>The admin queue for those reports: dismiss, remove the item, suspend or reinstate an account.</summary>
[Route("api/admin")]
public sealed class AdminModerationController(IModerationService moderation) : BaseApiController
{
    [HttpGet("reports")]
    [HasPermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct) =>
        Success(await moderation.ListAsync(status, ct));

    [HttpPost("reports/{reportId:guid}/resolve")]
    [HasPermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> Resolve(Guid reportId, [FromBody] ResolveContentReportRequest request, CancellationToken ct) =>
        Success(await moderation.ResolveAsync(reportId, request, ct));

    [HttpPut("users/{userId:guid}/suspended")]
    [HasPermission(Permissions.Admin.ManageUsers)]
    public async Task<IActionResult> Suspend(Guid userId, [FromBody] SetSuspendedRequest request, CancellationToken ct)
    {
        await moderation.SetSuspendedAsync(userId, request.Suspended, ct);
        return SuccessMessage(request.Suspended ? "Suspended." : "Reinstated.");
    }
}

public sealed record SetSuspendedRequest(bool Suspended);
