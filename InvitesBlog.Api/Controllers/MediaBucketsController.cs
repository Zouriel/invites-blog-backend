using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.MediaBuckets;
using InvitesBlog.Application.Services.MediaBuckets;
using InvitesBlog.Application.Services.Photos;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// Media buckets, from the owner's side: what they have, what it costs to make one bigger, and the
/// codes that let other people put things in it.
///
/// <para>Every route here takes a bucket id and none of them trust it. <see cref="Permissions.Buckets"/>
/// says only what KIND of thing the caller may do; which bucket is decided inside the service, by
/// ownership, on every single call.</para>
///
/// <para>The contribution side does NOT live here — see <see cref="BucketContributionController"/>.
/// Somebody who scanned a card on a table has no account and holds no permission, so mixing their
/// routes in among these would put an anonymous door inside an authorized controller.</para>
/// </summary>
[ApiController]
[Route("api/media-buckets")]
public sealed class MediaBucketsController(
    IMediaBucketService buckets, IEventPhotoService photos) : BaseApiController
{
    [HttpGet]
    [HasPermission(Permissions.Buckets.Read)]
    public async Task<IActionResult> Mine(CancellationToken ct) =>
        Success(await buckets.MineAsync(ct));

    /// <summary>
    /// The sizes on offer. Unscoped when no bucket is named — that is the price list someone reads
    /// before they own anything, which is exactly when they are deciding whether to.
    /// </summary>
    [HttpGet("plans")]
    [HasPermission(Permissions.Buckets.Read)]
    public async Task<IActionResult> Plans([FromQuery] Guid? bucketId, CancellationToken ct) =>
        Success(await buckets.PlansAsync(bucketId, ct));

    /// <summary>
    /// One bucket. The VIEW door — a member the owner let in gets the bucket itself, because the
    /// alternative is a page that can list photographs but not say whose night they are.
    /// </summary>
    /// <summary>
    /// The bucket belonging to an event, created on the spot if that event predates buckets.
    ///
    /// <para>This is how a host reaches their own event's bucket at all. The Events list shows only
    /// standalone buckets — an event already has a tile there, and listing its bucket beside it made
    /// one evening look like two — so without this route a campaign's size, contribution codes and
    /// guest list would have been reachable from nowhere.</para>
    /// </summary>
    [HttpGet("/api/campaigns/{campaignId:guid}/bucket")]
    [HasPermission(Permissions.Buckets.Read)]
    public async Task<IActionResult> ForCampaign(Guid campaignId, CancellationToken ct) =>
        Success(await buckets.ForCampaignOwnerAsync(campaignId, ct));

    /// <summary>
    /// Gives this event a bucket. Reading the dashboard deliberately does not — see
    /// <see cref="IMediaBucketService.ForCampaignOwnerAsync"/> — so this is the host saying yes.
    /// </summary>
    [HttpPost("/api/campaigns/{campaignId:guid}/bucket")]
    [HasPermission(Permissions.Buckets.Manage)]
    public async Task<IActionResult> CreateForCampaign(Guid campaignId, CancellationToken ct) =>
        Created(await buckets.CreateForCampaignAsync(campaignId, ct));

    [HttpGet("{bucketId:guid}")]
    [HasPermission(Permissions.Buckets.Read)]
    public async Task<IActionResult> Get(Guid bucketId, CancellationToken ct) =>
        Success(await buckets.ViewAsync(bucketId, ct));

    [HttpPost]
    [HasPermission(Permissions.Buckets.Manage)]
    public async Task<IActionResult> Create(
        [FromBody] CreateMediaBucketRequest req, CancellationToken ct) =>
        Created(await buckets.CreateAsync(req, ct));

    [HttpPatch("{bucketId:guid}")]
    [HasPermission(Permissions.Buckets.Manage)]
    public async Task<IActionResult> Update(
        Guid bucketId, [FromBody] UpdateMediaBucketRequest req, CancellationToken ct) =>
        Success(await buckets.UpdateAsync(bucketId, req, ct));

    /// <summary>
    /// Moves the bucket onto a size. <b>There is no payment behind this yet</b> — it grants the space
    /// outright. When checkout arrives it calls this, after it is paid, rather than replacing it.
    /// </summary>
    [HttpPost("{bucketId:guid}/tier")]
    [HasPermission(Permissions.Buckets.Manage)]
    public async Task<IActionResult> ChooseTier(
        Guid bucketId, [FromBody] ChooseMediaBucketTierRequest req, CancellationToken ct) =>
        Success(await buckets.ChooseTierAsync(bucketId, req, ct));

    // ---------- what is in it ----------

    /// <summary>
    /// The bucket's contents. Reached by BUCKET rather than by campaign, which is the only way a
    /// standalone one can be opened at all — otherwise somebody could buy a bucket with no event
    /// behind it and have no way to look inside it.
    /// </summary>
    [HttpGet("{bucketId:guid}/media")]
    [HasPermission(Permissions.Buckets.Read)]
    public async Task<IActionResult> Media(Guid bucketId, CancellationToken ct) =>
        Success(await photos.GetBucketAsync(bucketId, ct));

    /// <summary>
    /// Adds to the bucket as its owner. The same door as the dashboard's, addressed by bucket.
    /// </summary>
    [HttpPost("{bucketId:guid}/media")]
    [HasPermission(Permissions.Buckets.Manage)]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> AddMedia(
        Guid bucketId, [FromForm] IFormFileCollection? files, IFormFile? file, IFormFile? poster,
        CancellationToken ct)
    {
        var bucket = await buckets.GetAsync(bucketId, ct);   // ownership, or it throws — a member
                                                             // may look, never add on this door
        var uploads = (files is { Count: > 0 } ? files.AsEnumerable() : [])
            .Concat(file is not null ? [file] : Array.Empty<IFormFile>())
            .Where(f => f.Length > 0)
            .ToList();

        if (uploads.Count == 0)
            return BadRequest(Application.Common.ApiResponse<object?>.Fail("Pick at least one file."));

        // A still belongs to ONE clip — the same rule, and the same reason, as PhotosController.
        var still = poster is { Length: > 0 } ? await ReadAsync(poster, ct) : null;
        if (still is not null && uploads.Count > 1)
            return BadRequest(Application.Common.ApiResponse<object?>.Fail(
                "Send a clip and the still that stands for it on their own."));

        var added = new List<Application.Dtos.Photos.EventPhotoDto>();
        foreach (var upload in uploads)
            added.Add(await photos.AddToBucketAsync(
                bucketId, bucket.CampaignId, uploaderName: null, await ReadAsync(upload, ct),
                upload.ContentType, upload.FileName, still, ct));

        return Created(added);
    }

    [HttpDelete("{bucketId:guid}/media/{photoId:guid}")]
    [HasPermission(Permissions.Buckets.Manage)]
    public async Task<IActionResult> RemoveMedia(Guid bucketId, Guid photoId, CancellationToken ct)
    {
        await photos.DeleteFromBucketAsync(bucketId, photoId, ct);
        return SuccessMessage("Removed.");
    }

    // ---------- contribution codes ----------

    /// <summary>
    /// Every code ever made for this bucket, newest first. The newest live one is the one the
    /// dashboard keeps on show — a host who printed a card last week should not have to have kept
    /// the picture themselves.
    /// </summary>
    [HttpGet("{bucketId:guid}/qr")]
    [HasPermission(Permissions.Buckets.Read)]
    public async Task<IActionResult> Qrs(Guid bucketId, CancellationToken ct) =>
        Success(await buckets.QrsAsync(bucketId, ct));

    /// <summary>
    /// Makes a code. The response is the ONLY place its scannable URL ever appears — the token behind
    /// it is stored hashed. What stays available afterwards is the rendered image, which is the thing
    /// worth keeping anyway.
    /// </summary>
    [HttpPost("{bucketId:guid}/qr")]
    [HasPermission(Permissions.Buckets.Manage)]
    public async Task<IActionResult> CreateQr(
        Guid bucketId, [FromBody] CreateMediaBucketQrRequest req, CancellationToken ct) =>
        Created(await buckets.CreateQrAsync(bucketId, req, ct));

    /// <summary>
    /// Turns a code off. A printed card cannot be recalled, only refused — which is the whole reason
    /// codes are separate rows rather than one switch on the bucket.
    /// </summary>
    [HttpDelete("{bucketId:guid}/qr/{qrId:guid}")]
    [HasPermission(Permissions.Buckets.Manage)]
    public async Task<IActionResult> RevokeQr(Guid bucketId, Guid qrId, CancellationToken ct)
    {
        await buckets.RevokeQrAsync(bucketId, qrId, ct);
        return SuccessMessage("That code no longer works.");
    }

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }
}
