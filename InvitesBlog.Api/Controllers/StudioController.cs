using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Studio;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>A Studio account's page: its clients' events and what they pay. The service checks the plan.</summary>
[Route("api/studio")]
public sealed class StudioController(IStudioService studio) : BaseApiController
{
    [HttpGet]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Overview(CancellationToken ct) =>
        Success(await studio.OverviewAsync(ct));
}
