using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Privacy;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>§15.3 Privacy — guest self-service removal. Public: the invite token in the URL is the
/// credential (Public role holds <c>privacy.remove</c>, so anonymous callers pass).</summary>
[Route("api/privacy")]
public sealed class PrivacyController(IPrivacyService privacy) : BaseApiController
{
    [HttpGet("remove/{token}")]
    [HasPermission(Permissions.Privacy.Remove)]
    public async Task<IActionResult> GetRemovalInfo(string token, CancellationToken ct) =>
        Success(await privacy.GetRemovalInfoAsync(token, ct));

    [HttpPost("remove/{token}")]
    [HasPermission(Permissions.Privacy.Remove)]
    public async Task<IActionResult> Remove(string token, CancellationToken ct) =>
        Success(await privacy.RemoveAsync(token, ct));
}
