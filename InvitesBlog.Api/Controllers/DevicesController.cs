using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Accounts;
using InvitesBlog.Application.Services.Accounts;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// The signed-in account's devices for push notifications. Limited per address by "openlink"
/// (30 a minute): plenty for a household or a venue's Wi-Fi, and it stops a client filling the table.
/// </summary>
[Route("api/me/devices")]
public sealed class DevicesController(IDeviceTokenService devices) : BaseApiController
{
    [HttpPost]
    [HasPermission(Permissions.Templates.Read)]
    [EnableRateLimiting("openlink")]
    public async Task<IActionResult> Register([FromBody] RegisterDeviceRequest request, CancellationToken ct)
    {
        await devices.RegisterAsync(request, ct);
        return SuccessMessage("Device registered.");
    }

    [HttpDelete]
    [HasPermission(Permissions.Templates.Read)]
    [EnableRateLimiting("openlink")]
    public async Task<IActionResult> Remove([FromBody] RemoveDeviceRequest request, CancellationToken ct)
    {
        await devices.RemoveAsync(request, ct);
        return SuccessMessage("Device removed.");
    }
}
