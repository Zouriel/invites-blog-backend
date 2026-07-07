using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Guests;
using InvitesBlog.Application.Services.Guests;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Infrastructure.Delivery;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// §10.4 Guest upload + post-payment add/fix/resend. Thin controller — delegates business logic to
/// <see cref="IGuestService"/>. The actual invite send is performed by the Infrastructure
/// <see cref="DispatchService"/> (which the Application layer cannot reference).
/// </summary>
[Route("api/campaigns/{id:guid}/guests")]
public sealed class GuestsController(IGuestService guests, DispatchService dispatch) : BaseApiController
{
    // POST /api/campaigns/{id}/guests/upload — multipart xlsx + defaultCountry (§10.4)
    [HttpPost("upload")]
    [HasPermission(Permissions.Guests.Upload)]
    public async Task<IActionResult> Upload(
        Guid id, IFormFile file, [FromForm] string defaultCountry = "MV", CancellationToken ct = default)
    {
        await using var stream = file.OpenReadStream();
        var summary = await guests.UploadAsync(id, stream, file.FileName, defaultCountry, ct);
        return Success(summary);
    }

    // GET /api/campaigns/{id}/guests/upload/{uploadId}/errors.csv — downloadable report (§4.4.6)
    [HttpGet("upload/{uploadId:guid}/errors.csv")]
    [HasPermission(Permissions.Guests.Read)]
    public async Task<IActionResult> ErrorsCsv(Guid id, Guid uploadId, CancellationToken ct)
    {
        var csv = await guests.ExportErrorsCsvAsync(id, uploadId, ct);
        return File(csv, "text/csv", "guests.csv");
    }

    // POST /api/campaigns/{id}/guests/confirm-upload — materialize guests, honoring suppression (§15.3)
    [HttpPost("confirm-upload")]
    [HasPermission(Permissions.Guests.Upload)]
    public async Task<IActionResult> ConfirmUpload(Guid id, [FromBody] ConfirmUploadRequest req, CancellationToken ct) =>
        Success(await guests.ConfirmUploadAsync(id, req, ct));

    // POST /api/campaigns/{id}/guests — manual add after payment; consumes prepaid capacity first (§4.7.4)
    [HttpPost]
    [HasPermission(Permissions.Guests.Write)]
    public async Task<IActionResult> Add(Guid id, [FromBody] AddGuestRequest req, CancellationToken ct)
    {
        var outcome = await guests.AddGuestAsync(id, req, ct);
        if (outcome.DispatchGuestId is Guid dispatchGuestId)
            await dispatch.ResendAsync(dispatchGuestId, ct);
        return Success(outcome.Response);
    }

    // PUT /api/campaigns/{id}/guests/{guestId} — fix contact details (§4.7.4)
    [HttpPut("{guestId:guid}")]
    [HasPermission(Permissions.Guests.Write)]
    public async Task<IActionResult> Update(
        Guid id, Guid guestId, [FromBody] UpdateGuestRequest req, CancellationToken ct)
    {
        await guests.UpdateGuestAsync(id, guestId, req, ct);
        return NoContent();
    }

    // POST /api/campaigns/{id}/guests/{guestId}/resend — free, max 3 per 24h (§4.7.4)
    [HttpPost("{guestId:guid}/resend")]
    [HasPermission(Permissions.Guests.Resend)]
    public async Task<IActionResult> Resend(Guid id, Guid guestId, CancellationToken ct)
    {
        await guests.PrepareResendAsync(id, guestId, ct);
        var sent = await dispatch.ResendAsync(guestId, ct);
        return Success(new ResendResultDto(sent));
    }
}
