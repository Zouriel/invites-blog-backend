using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Venues;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// The venue the signed-in account owns or works at: its name and logo, its staff, and its events.
/// The service checks who may do what; only the owner changes the venue itself.
/// </summary>
[Route("api/venue")]
public sealed class VenueController(IVenueService venues) : BaseApiController
{
    [HttpGet]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Get(CancellationToken ct) => Success(await venues.GetAsync(ct));

    [HttpPut]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> Update([FromBody] UpdateVenueProfileRequest req, CancellationToken ct) =>
        Success(await venues.UpdateAsync(req, ct));

    [HttpPost("logo")]
    [HasPermission(Permissions.Campaigns.Write)]
    [RequestSizeLimit(6 * 1024 * 1024)]
    public async Task<IActionResult> SetLogo(IFormFile? file, CancellationToken ct)
    {
        if (file is null || file.Length == 0) return BadRequest(Application.Common.ApiResponse<object?>.Fail("Choose an image."));
        using var ms = new MemoryStream();
        await file.CopyToAsync(ms, ct);
        return Success(await venues.SetLogoAsync(ms.ToArray(), file.ContentType, file.FileName, ct));
    }

    [HttpDelete("logo")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> RemoveLogo(CancellationToken ct) => Success(await venues.RemoveLogoAsync(ct));

    [HttpPost("staff")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> AddStaff([FromBody] AddVenueStaffRequest req, CancellationToken ct) =>
        Success(await venues.AddStaffAsync(req, ct));

    [HttpDelete("staff/{staffId:guid}")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> RemoveStaff(Guid staffId, CancellationToken ct) =>
        Success(await venues.RemoveStaffAsync(staffId, ct));

    /// <summary>A new event at the venue, with its first album.</summary>
    [HttpPost("events")]
    [HasPermission(Permissions.Campaigns.Create)]
    public async Task<IActionResult> CreateEvent([FromBody] CreateVenueEventRequest req, CancellationToken ct) =>
        Success(await venues.CreateEventAsync(req, ct));
}
