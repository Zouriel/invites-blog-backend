using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.SaveTheDate;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>From a save the date to its invitation.</summary>
[Route("api/campaigns/{campaignId:guid}/invitation")]
public sealed class SaveTheDateController(ISaveTheDateService saveTheDates) : BaseApiController
{
    /// <summary>Makes the invitation (guests, pass and extra emails come along), or returns the one already made.</summary>
    [HttpPost]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> Make(Guid campaignId, CancellationToken ct) =>
        Success(await saveTheDates.MakeInvitationAsync(campaignId, ct));
}
