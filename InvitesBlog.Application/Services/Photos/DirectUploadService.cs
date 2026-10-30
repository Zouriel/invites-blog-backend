using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Photos;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.MediaBuckets;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Application.Services.Photos;

/// <summary>Who an upload is for, once a door has decided they may add.</summary>
public sealed record UploadDoor(Guid BucketId, Guid? CampaignId, Guid? GuestId, string? UploaderName, Guid? QrId = null);

public sealed record DirectUploadRequest(string? FileName, string? ContentType, long Size, string? Ticket = null);

/// <summary><paramref name="Direct"/> false means: send this file the old way.</summary>
public sealed record DirectUploadTicket(bool Direct, Guid? Id = null, string? Url = null, string? ContentType = null);

public sealed record DirectUploadStatusDto(Guid Id, string Status, string? Error = null, EventPhotoDto? Photo = null);

/// <summary>
/// Photographs sent straight from the phone to the holding area, then checked and stored here.
/// The phone asks for a link (door checks, space reserved), uploads, says it has, and the server takes
/// over: <see cref="FinishAsync"/> runs in the background, so a guest can put the phone away.
/// </summary>
public interface IDirectUploadService
{
    /// <summary>Whether pages should allow for uploads to the holding area at all.</summary>
    string? UploadOrigin { get; }

    Task<DirectUploadTicket> IssueAsync(UploadDoor door, DirectUploadRequest request, CancellationToken ct = default);
    Task<DirectUploadStatusDto> CompleteAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<DirectUploadStatusDto>> StatusAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>One upload the processor has claimed. Never throws for a bad file: it records the outcome.</summary>
    Task FinishAsync(Guid id, CancellationToken ct = default);

    /// <summary>Rescues links that lapsed after the bytes arrived, and gives back space held by the rest.</summary>
    Task SweepAsync(CancellationToken ct = default);
}

public sealed class DirectUploadService(
    IRepository<PendingUpload> uploads,
    IRepository<EventPhoto> photos,
    IIncomingUploadStore incoming,
    IEventPhotoService photoService,
    IMediaBucketService buckets,
    IUnitOfWork uow,
    IConfiguration config,
    ILogger<DirectUploadService>? logger = null) : IDirectUploadService
{
    /// <summary>Long enough for a slow connection to send one large photo; a queue asks per file.</summary>
    public static readonly TimeSpan LinkLifetime = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Above this a file takes the old path rather than being refused. Photographs stay uncapped (see
    /// EventPhotoService); this only bounds what the processor holds in memory on someone else's say-so.
    /// </summary>
    public const long MaxDirectBytes = 100L * 1024 * 1024;

    public const int MaxAttempts = 3;
    private const int MaxStatusIds = 50;

    public string? UploadOrigin => incoming.Available ? incoming.Origin : null;

    /// <summary>
    /// The switch: "on" for everyone, otherwise only the albums named in Uploads:DirectBuckets. Turning
    /// it off only stops new links, so anything already on its way still finishes.
    /// </summary>
    private bool EnabledFor(Guid bucketId)
    {
        if (!incoming.Available) return false;
        if (string.Equals(config["Uploads:Direct"], "on", StringComparison.OrdinalIgnoreCase)) return true;
        return (config["Uploads:DirectBuckets"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Any(b => Guid.TryParse(b, out var g) && g == bucketId);
    }

    public async Task<DirectUploadTicket> IssueAsync(
        UploadDoor door, DirectUploadRequest request, CancellationToken ct = default)
    {
        var type = (request.ContentType ?? string.Empty).Trim().ToLowerInvariant();
        // Videos and files with no type keep the old path. The bytes are judged later either way; this
        // only decides the route, and a value signed into a header must be a plain media type.
        if (!EnabledFor(door.BucketId)
            || !type.StartsWith("image/", StringComparison.Ordinal) || type.Contains("svg", StringComparison.Ordinal)
            || type.Length > 100 || !type.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '+' or '.')
            || request.Size <= 0 || request.Size > MaxDirectBytes)
            return new DirectUploadTicket(false);

        await buckets.EnsureOpenAsync(door.BucketId, ct);
        await buckets.ReserveRoomAsync(door.BucketId, request.Size, ct);

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var key = $"incoming/{door.BucketId:N}/{id:N}";
        try
        {
            await uploads.AddAsync(new PendingUpload
            {
                Id = id,
                BucketId = door.BucketId,
                CampaignId = door.CampaignId,
                GuestId = door.GuestId,
                UploaderName = door.UploaderName,
                QrId = door.QrId,
                IncomingKey = key,
                ContentType = type,
                FileName = Path.GetFileName(request.FileName ?? string.Empty) is { Length: > 0 and <= 200 } name ? name : "photo",
                DeclaredBytes = request.Size,
                Status = PendingUploadStatus.Issued,
                ExpiresAt = now + LinkLifetime,
                NextAttemptAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            }, ct);
            await uow.SaveChangesAsync(ct);
            return new DirectUploadTicket(true, id, incoming.PresignPut(key, type, request.Size, LinkLifetime), type);
        }
        catch
        {
            await buckets.CountUsageAsync(door.BucketId, -request.Size, CancellationToken.None);
            throw;
        }
    }

    public async Task<DirectUploadStatusDto> CompleteAsync(Guid id, CancellationToken ct = default)
    {
        var upload = await uploads.Query(tracking: true).FirstOrDefaultAsync(u => u.Id == id, ct)
                     ?? throw new NotFoundException("That upload isn't here.");

        // Saying so twice is not an error: a retry after a dropped response lands here.
        if (upload.Status == PendingUploadStatus.Issued) await AcceptIfArrivedAsync(upload, ct);
        return (await StatusAsync([id], ct))[0];
    }

    /// <summary>Moves it on if the right bytes are there. A body of any other size is refused for good.</summary>
    private async Task AcceptIfArrivedAsync(PendingUpload upload, CancellationToken ct)
    {
        var size = await incoming.SizeAsync(upload.IncomingKey, ct);
        if (size is null) return;
        if (size != upload.DeclaredBytes)
        {
            await GiveUpAsync(upload, PendingUploadStatus.Failed, "That file didn't arrive whole. Please try again.", ct);
            return;
        }
        upload.Status = PendingUploadStatus.Uploaded;
        upload.NextAttemptAt = upload.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<DirectUploadStatusDto>> StatusAsync(
        IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        var wanted = ids.Distinct().Take(MaxStatusIds).ToList();
        var rows = await uploads.Query().Where(u => wanted.Contains(u.Id)).ToListAsync(ct);
        var doneIds = rows.Where(r => r.Status == PendingUploadStatus.Done).Select(r => r.Id).ToList();
        var done = doneIds.Count == 0
            ? []
            : await photos.Query().Where(p => doneIds.Contains(p.Id) && p.DeletedAt == null).ToListAsync(ct);

        return rows.Select(r =>
        {
            var p = done.FirstOrDefault(x => x.Id == r.Id);
            return new DirectUploadStatusDto(
                r.Id,
                r.Status switch
                {
                    PendingUploadStatus.Issued => "issued",
                    PendingUploadStatus.Done => "done",
                    PendingUploadStatus.Failed => "failed",
                    PendingUploadStatus.Expired => "expired",
                    _ => "processing",
                },
                r.Error,
                p is null ? null : new EventPhotoDto(
                    p.Id, p.Url, p.ThumbUrl, p.OriginalUrl, p.ContentType, p.Width, p.Height, p.UploaderName, true, p.CreatedAt));
        }).ToList();
    }

    public async Task FinishAsync(Guid id, CancellationToken ct = default)
    {
        var upload = await uploads.Query(tracking: true).FirstOrDefaultAsync(u => u.Id == id, ct);
        if (upload is not { Status: PendingUploadStatus.Processing }) return;

        try
        {
            // Done already and stopped before saying so: the fixed id is how a restart can tell.
            if (!await photos.Query().AnyAsync(p => p.Id == id, ct))
            {
                var bytes = await incoming.GetAsync(upload.IncomingKey, ct);
                if (bytes is null || bytes.Length != upload.DeclaredBytes)
                {
                    await GiveUpAsync(upload, PendingUploadStatus.Failed, "That file didn't arrive whole. Please try again.", ct);
                    return;
                }

                await photoService.StoreUploadedAsync(
                    id, new UploadDoor(upload.BucketId, upload.CampaignId, upload.GuestId, upload.UploaderName),
                    bytes, upload.ContentType, upload.FileName, ct);
                if (upload.QrId is { } qr) await buckets.CountContributionAsync(qr, ct);
            }

            upload.Status = PendingUploadStatus.Done;
            upload.UpdatedAt = DateTimeOffset.UtcNow;
            await uow.SaveChangesAsync(ct);
            await DeleteIncomingAsync(upload);
        }
        catch (AppException e)
        {
            // The file itself was refused. Trying again would refuse it again.
            await GiveUpAsync(upload, PendingUploadStatus.Failed, e.Message, CancellationToken.None);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down: the startup sweep picks it back up.
            throw;
        }
        catch (Exception e)
        {
            logger?.LogError(e, "Direct upload {UploadId}: attempt {Attempt} failed.", id, upload.Attempts + 1);
            upload.Attempts++;
            if (upload.Attempts >= MaxAttempts)
            {
                await GiveUpAsync(upload, PendingUploadStatus.Failed, "We couldn't save that photo. Please try again.", CancellationToken.None);
                return;
            }
            upload.Status = PendingUploadStatus.Uploaded;
            upload.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(15 * upload.Attempts);
            upload.UpdatedAt = DateTimeOffset.UtcNow;
            await uow.SaveChangesAsync(CancellationToken.None);
        }
    }

    public async Task SweepAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var lapsed = await uploads.Query(tracking: true)
            .Where(u => u.Status == PendingUploadStatus.Issued && u.ExpiresAt < now)
            .OrderBy(u => u.ExpiresAt).Take(100).ToListAsync(ct);

        foreach (var upload in lapsed)
        {
            // The phone may have uploaded and lost signal before saying so. That photo is not lost.
            await AcceptIfArrivedAsync(upload, ct);
            if (upload.Status == PendingUploadStatus.Issued)
                await GiveUpAsync(upload, PendingUploadStatus.Expired, null, ct);
        }
    }

    /// <summary>The one way out that isn't success: the space goes back and the file goes.</summary>
    private async Task GiveUpAsync(PendingUpload upload, PendingUploadStatus status, string? error, CancellationToken ct)
    {
        upload.Status = status;
        upload.Error = error;
        upload.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
        logger?.LogWarning("Direct upload {UploadId} ended {Status}: {Error}", upload.Id, status, error ?? "-");

        try
        {
            await buckets.CountUsageAsync(upload.BucketId, -upload.DeclaredBytes, CancellationToken.None);
        }
        catch (Exception e)
        {
            // The album may have been deleted in the meantime, and then nothing is owed.
            logger?.LogWarning(e, "Direct upload {UploadId}: could not give its space back.", upload.Id);
        }
        await DeleteIncomingAsync(upload);
    }

    /// <summary>Best effort: the bucket's own expiry rule removes anything this misses.</summary>
    private async Task DeleteIncomingAsync(PendingUpload upload)
    {
        try
        {
            await incoming.DeleteAsync(upload.IncomingKey, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger?.LogWarning(e, "Direct upload {UploadId}: could not delete the incoming file.", upload.Id);
        }
    }
}
