using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Studio;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>A Studio account's page: its passes and its clients' events. The service checks the plan.</summary>
[Route("api/studio")]
public sealed class StudioController(IStudioService studio) : BaseApiController
{
    [HttpGet]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Overview(CancellationToken ct) =>
        Success(await studio.OverviewAsync(ct));

    /// <summary>Gives one of the Studio's passes to a client's event.</summary>
    [HttpPost("clients/{campaignId:guid}/pass")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> GivePass(Guid campaignId, [FromBody] GivePassRequest req, CancellationToken ct) =>
        Success(await studio.GivePassAsync(campaignId, req, ct));
}
