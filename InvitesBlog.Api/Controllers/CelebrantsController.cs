using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Services.Celebrants;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>The people an event is for. Every action re-checks access in the service.</summary>
[Route("api/campaigns/{campaignId:guid}/celebrants")]
public sealed class CelebrantsController(ICelebrantService celebrants) : BaseApiController
{
    [HttpGet]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> List(Guid campaignId, CancellationToken ct) =>
        Success(await celebrants.ListAsync(campaignId, ct));

    [HttpPost]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> Add(Guid campaignId, [FromBody] AddCelebrantRequest req, CancellationToken ct) =>
        Success(await celebrants.AddAsync(campaignId, req, ct));

    [HttpDelete("{celebrantId:guid}")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> Remove(Guid campaignId, Guid celebrantId, CancellationToken ct) =>
        Success(await celebrants.RemoveAsync(campaignId, celebrantId, ct));

    [HttpPut("{celebrantId:guid}/access")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> SetAccess(
        Guid campaignId, Guid celebrantId, [FromBody] SetCelebrantAccessRequest req, CancellationToken ct) =>
        Success(await celebrants.SetAccessAsync(campaignId, celebrantId, req, ct));

    [HttpPost("{celebrantId:guid}/notify")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> Notify(Guid campaignId, Guid celebrantId, CancellationToken ct) =>
        Success(await celebrants.NotifyAsync(campaignId, celebrantId, ct));
}
