using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Guidance;
using InvitesBlog.Application.Services.Guidance;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Infrastructure.Demo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// Tips, tours and offers on the account, and the read-only demo event. The contract is
/// docs/GUIDANCE.md section 2 (shared with the web app and the Flutter app); reads come in the usual
/// envelope (<c>{ success, data }</c>), writes answer 204.
/// </summary>
[Route("api/me")]
public sealed class GuidanceController(IGuidanceService guidance) : BaseApiController
{
    /// <summary>tipsOn, seen, welcomeUntil, progress and newRoles. 401 without a signed-in account.</summary>
    [HttpGet("guidance")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> Get(CancellationToken ct) => Success(await guidance.GetAsync(ct));

    /// <summary>Adds ids to seen. Idempotent; 400 on a bad id or more than 50.</summary>
    [HttpPost("guidance/seen")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> Seen([FromBody] MarkGuidanceSeenRequest req, CancellationToken ct)
    {
        await guidance.MarkSeenAsync(req, ct);
        return NoContent();
    }

    /// <summary>Switches tips on or off.</summary>
    [HttpPut("guidance")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> Set([FromBody] SetGuidanceRequest req, CancellationToken ct)
    {
        await guidance.SetAsync(req, ct);
        return NoContent();
    }

    /// <summary>"Show tips again": seen emptied, tips on.</summary>
    [HttpPost("guidance/reset")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> Reset(CancellationToken ct)
    {
        await guidance.ResetAsync(ct);
        return NoContent();
    }

    /// <summary>
    /// The "you've been added" card was seen or dismissed: the event leaves newRoles. 204 whether or not
    /// the account is a celebrant on it, so it says nothing about events it isn't on.
    /// </summary>
    [HttpPost("roles/{campaignId:guid}/ack")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> Acknowledge(Guid campaignId, CancellationToken ct)
    {
        await guidance.AcknowledgeRoleAsync(campaignId, ct);
        return NoContent();
    }

    /// <summary>
    /// The sample wedding "See an example event" opens (docs/GUIDANCE.md section 7): the organiser
    /// dashboard's shape plus demo: true. Public and the same for everybody, so caches may keep it.
    /// </summary>
    [HttpGet("/api/demo/event")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Any)]
    public IActionResult Demo([FromServices] DemoEventBuilder demo) => Success(demo.Build(DateTimeOffset.UtcNow));
}
