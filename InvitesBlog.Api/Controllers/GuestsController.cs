using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Guests;
using InvitesBlog.Application.Services.Guests;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Infrastructure.Delivery;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// §10.4 Guest upload + add/fix/resend. Thin controller — delegates business logic to
/// <see cref="IGuestService"/>. The actual invite send is performed by the Infrastructure
/// <see cref="DispatchService"/> (which the Application layer cannot reference).
/// </summary>
[Route("api/campaigns/{id:guid}/guests")]
public sealed class GuestsController(IGuestService guests, DispatchService dispatch, IManualSendService manualSends) : BaseApiController
{
    // POST /api/campaigns/{id}/guests/upload — multipart xlsx + optional defaultCountry/phoneCountry (ISO2) (§10.4).
    // No country given: the account's, else the request's (CF-IPCountry); there is no built-in default.
    [HttpPost("upload")]
    [HasPermission(Permissions.Guests.Upload)]
    public async Task<IActionResult> Upload(
        Guid id, IFormFile file, [FromForm] string? defaultCountry = null, [FromForm] string? phoneCountry = null,
        CancellationToken ct = default)
    {
        await using var stream = file.OpenReadStream();
        var summary = await guests.UploadAsync(id, stream, file.FileName, phoneCountry ?? defaultCountry, ct);
        return Success(summary);
    }

    // POST /api/campaigns/{id}/guests/confirm-upload — materialize guests, honoring suppression (§15.3)
    [HttpPost("confirm-upload")]
    [HasPermission(Permissions.Guests.Upload)]
    public async Task<IActionResult> ConfirmUpload(Guid id, [FromBody] ConfirmUploadRequest req, CancellationToken ct) =>
        Success(await guests.ConfirmUploadAsync(id, req, ct));

    // POST /api/campaigns/{id}/guests — manual add; sent straight away once the invitations are out (§4.7.4)
    [HttpPost]
    [HasPermission(Permissions.Guests.Write)]
    public async Task<IActionResult> Add(Guid id, [FromBody] AddGuestRequest req, CancellationToken ct)
    {
        var outcome = await guests.AddGuestAsync(id, req, ct);
        var response = outcome.Response;
        if (outcome.DispatchGuestId is Guid dispatchGuestId)
        {
            // Surface what actually happened (provider accept/reject), not just "we tried" — the
            // response previously always looked like a bare add even when a send was attempted.
            var sent = await dispatch.ResendAsync(dispatchGuestId, ct);
            response = response with { Sent = sent };
        }
        return Success(response);
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

    // DELETE /api/campaigns/{id}/guests/{guestId} — only someone who added themselves from the open guest list
    [HttpDelete("{guestId:guid}")]
    [HasPermission(Permissions.Guests.Write)]
    public async Task<IActionResult> RemoveSelfAdded(Guid id, Guid guestId, CancellationToken ct)
    {
        await guests.RemoveSelfAddedAsync(id, guestId, ct);
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

    // POST /api/campaigns/{id}/guests/{guestId}/manual-send — {channel: whatsapp|viber|telegram}.
    // The host is sending this guest their invitation by hand from their own phone: records it
    // ("<channel>-manual", shown on every device) and returns {link, text, sentAt} to put in the app.
    // Free: not an emailed invitation, and not counted in the resend limit.
    [HttpPost("{guestId:guid}/manual-send")]
    [HasPermission(Permissions.Guests.Resend)]
    public async Task<IActionResult> ManualSend(
        Guid id, Guid guestId, [FromBody] ManualSendRequest req, CancellationToken ct) =>
        Success(await manualSends.RecordAsync(id, guestId, req, ct));
}
