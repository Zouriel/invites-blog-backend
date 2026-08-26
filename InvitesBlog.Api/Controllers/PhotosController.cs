using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Invites;
using InvitesBlog.Application.Services.Photos;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// The event photo box (§5), for the two callers that reach it over the API: the host on their
/// dashboard, and a guest whose invitation is open in the Angular app.
///
/// <para>The third caller — a guest on a server-rendered invitation — does NOT come through here.
/// That surface has no browser-facing API by design (§2), so its photo box is a rendered page in
/// <c>GuestController</c> authorized by the render cookie. Same service underneath; different door.</para>
///
/// <para><b>Both routes below take a campaign id, and neither trusts it.</b> The host route proves
/// ownership; the guest route resolves which guest the caller IS on that campaign and refuses if the
/// answer is "none". Permissions here say only what KIND of thing the caller may do.</para>
///
/// <para><b>The upload actions carry no size ceiling.</b> Both framework limits are lifted here and
/// only here: <c>DisableRequestSizeLimit</c> for Kestrel's 30 MB default, and <c>RequestFormLimits</c>
/// for the 24 MB multipart cap configured globally. That global cap is the TEMPLATE-image ceiling and
/// it stays — limiting an image a template renders is right, because past a point the extra pixels are
/// ones no browser will show and every guest pays to download them. A guest's photograph of the event
/// is the opposite: nobody's memory of the night should be refused for being large.</para>
///
/// <para>What remains above this is infrastructure, not application: whatever body limit and timeout
/// the reverse proxy in front of the API imposes. Those are a deployment setting, not something a
/// controller attribute can lift.</para>
/// </summary>
[ApiController]
public sealed class PhotosController(IEventPhotoService photos, IInviteService invites) : BaseApiController
{
    // ---------- the host, from their dashboard ----------

    [HttpGet("/api/campaigns/{campaignId:guid}/photos")]
    [HasPermission(Permissions.Photos.Read)]
    public async Task<IActionResult> ForHost(Guid campaignId, CancellationToken ct) =>
        Success(await photos.GetAsync(campaignId, viewerGuestId: null, ct));

    [HttpPost("/api/campaigns/{campaignId:guid}/photos")]
    [HasPermission(Permissions.Photos.Upload)]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> AddAsHost(
        Guid campaignId, [FromForm] IFormFileCollection? files, IFormFile? file, CancellationToken ct) =>
        await AddAsync(campaignId, guestId: null, files, file, ct);

    [HttpDelete("/api/campaigns/{campaignId:guid}/photos/{photoId:guid}")]
    [HasPermission(Permissions.Photos.Read)]
    public async Task<IActionResult> RemoveAsHost(Guid campaignId, Guid photoId, CancellationToken ct)
    {
        await photos.DeleteAsync(campaignId, photoId, actingGuestId: null, ct);
        return SuccessMessage("Photo removed.");
    }

    /// <summary>
    /// The whole box, or the ones named in <paramref name="ids"/>, as a zip of the originals.
    /// </summary>
    [HttpGet("/api/campaigns/{campaignId:guid}/photos/download")]
    [HasPermission(Permissions.Photos.Read)]
    public Task DownloadAsHost(Guid campaignId, [FromQuery] Guid[]? ids, CancellationToken ct) =>
        WriteArchiveAsync(campaignId, viewerGuestId: null, ids, ct);

    // ---------- a guest, from an invitation open in the app ----------

    [HttpGet("/api/me/invitations/{campaignId:guid}/photos")]
    [HasPermission(Permissions.Photos.Read)]
    public async Task<IActionResult> ForGuest(Guid campaignId, CancellationToken ct) =>
        Success(await photos.GetAsync(campaignId, await GuestAsync(campaignId, ct), ct));

    [HttpPost("/api/me/invitations/{campaignId:guid}/photos")]
    [HasPermission(Permissions.Photos.Upload)]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> AddAsGuest(
        Guid campaignId, [FromForm] IFormFileCollection? files, IFormFile? file, CancellationToken ct) =>
        await AddAsync(campaignId, await GuestAsync(campaignId, ct), files, file, ct);

    [HttpDelete("/api/me/invitations/{campaignId:guid}/photos/{photoId:guid}")]
    [HasPermission(Permissions.Photos.Read)]
    public async Task<IActionResult> RemoveAsGuest(Guid campaignId, Guid photoId, CancellationToken ct)
    {
        await photos.DeleteAsync(campaignId, photoId, await GuestAsync(campaignId, ct), ct);
        return SuccessMessage("Photo removed.");
    }

    /// <summary>
    /// The same archive for a guest. Anyone who can see the gallery can keep what is in it — the
    /// people in these photographs were all at the same party.
    /// </summary>
    [HttpGet("/api/me/invitations/{campaignId:guid}/photos/download")]
    [HasPermission(Permissions.Photos.Read)]
    public async Task DownloadAsGuest(Guid campaignId, [FromQuery] Guid[]? ids, CancellationToken ct) =>
        await WriteArchiveAsync(campaignId, await GuestAsync(campaignId, ct), ids, ct);

    // ---------- shared ----------

    /// <summary>
    /// Which guest the caller is on this campaign. Never taken from the request — a guest id is the
    /// whole authorization for the guest door, so it is resolved from the caller's own verified
    /// identifiers every time.
    /// </summary>
    private async Task<Guid?> GuestAsync(Guid campaignId, CancellationToken ct) =>
        await invites.MyGuestIdAsync(campaignId, ct);

    /// <summary>
    /// Streams the zip straight into the response.
    ///
    /// <para>Written to the body rather than buffered and returned: an event's originals are full-
    /// resolution photographs and a whole box can run to gigabytes, which is a number the server must
    /// never be holding in memory on behalf of one guest tapping a button. The consequence is that the
    /// status code and headers are committed BEFORE the first photo is read, so a failure part-way
    /// through can only truncate the download — hence the authorization and the emptiness check both
    /// happen in the service before anything is written.</para>
    /// </summary>
    private async Task WriteArchiveAsync(
        Guid campaignId, Guid? viewerGuestId, Guid[]? ids, CancellationToken ct)
    {
        // Buffered off, or Kestrel holds the whole archive to work out Content-Length.
        var buffering = HttpContext.Features.Get<IHttpResponseBodyFeature>();
        buffering?.DisableBuffering();

        await photos.WriteArchiveAsync(campaignId, viewerGuestId, ids, name =>
        {
            // Runs once, after the service has authorized the caller and found something to send.
            Response.ContentType = "application/zip";
            Response.Headers.ContentDisposition = $"attachment; filename=\"{name}\"";
            return Response.Body;
        }, ct);
    }

    /// <summary>
    /// A phone's photo picker sends several at once, so the multi-file field is the normal case here
    /// — unlike the builder's slot upload, where one is.
    /// </summary>
    private async Task<IActionResult> AddAsync(
        Guid campaignId, Guid? guestId, IFormFileCollection? files, IFormFile? file, CancellationToken ct)
    {
        var uploads = (files is { Count: > 0 } ? files.AsEnumerable() : [])
            .Concat(file is not null ? [file] : Array.Empty<IFormFile>())
            .Where(f => f.Length > 0)
            .ToList();

        if (uploads.Count == 0)
            return BadRequest(Application.Common.ApiResponse<object?>.Fail("Pick at least one photo."));

        var added = new List<Application.Dtos.Photos.EventPhotoDto>();
        foreach (var upload in uploads)
        {
            await using var stream = upload.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            added.Add(await photos.AddAsync(
                campaignId, guestId, buffer.ToArray(), upload.ContentType, upload.FileName, ct));
        }

        return Created(added);
    }
}
