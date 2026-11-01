using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Common;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Accounts;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Video;

/// <summary>
/// Makes stored videos smaller and removes what the camera wrote into them. In the API host because
/// there is no worker in production; one at a time, at low priority, because the server is shared.
///
/// <para>The video can be watched from its first file the whole time. Only a copy that came out smaller
/// and that plays replaces it. The swap is one transaction (row, job, album space, removal of the old
/// file), so a stop at any point leaves either the old state or the new one. Everything else that can go
/// wrong ends with the first file kept.</para>
/// </summary>
public sealed class VideoTrimService(
    IServiceProvider services,
    IVideoTools tools,
    IConfiguration config,
    PendingRemovalSignal removals,
    ILogger<VideoTrimService> logger) : BackgroundService
{
    public const int MaxAttempts = 3;
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan NoRoomWait = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan GiveUpOnRoomAfter = TimeSpan.FromDays(3);

    /// <summary>
    /// How long the old file stays after a swap, for anyone with the old page open and playing it. No
    /// wait when the old file still has its camera metadata: then getting rid of it matters more.
    /// </summary>
    public static readonly TimeSpan OldFileGrace = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(config["Video:Trim"] ?? "on", "on", StringComparison.OrdinalIgnoreCase)) return;
        if (!tools.Available) return;

        // Let the app finish starting (and migrating) first.
        await Task.Delay(TimeSpan.FromSeconds(25), stoppingToken);
        await ReleaseInterruptedAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var worked = false;
            try
            {
                worked = await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The video trim pass failed.");
            }
            if (!worked) await Task.Delay(Idle, stoppingToken);
        }
    }

    /// <summary>
    /// At startup, before anything is running: what was mid-way goes back in the queue and its files go.
    /// The attempt it used stays counted, so a file that takes the process down can't do so for ever.
    /// </summary>
    public async Task ReleaseInterruptedAsync(CancellationToken ct)
    {
        tools.ClearWorkDir();
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.VideoTrimJobs.Where(j => j.Status == VideoTrimStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, VideoTrimStatus.Waiting), ct);
    }

    /// <summary>One job if one is due. False when there was nothing to do.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IStorageService>();

        var now = DateTimeOffset.UtcNow;
        var job = await db.VideoTrimJobs.AsNoTracking()
            .Where(j => j.Status == VideoTrimStatus.Waiting && j.NextAttemptAt <= now)
            .OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        if (job is null) return false;

        var claimed = await db.VideoTrimJobs
            .Where(j => j.Id == job.Id && j.Status == VideoTrimStatus.Waiting)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, VideoTrimStatus.Running)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (claimed == 0) return true;

        // Used up by stops and crashes alone, since those are the only ways back into the queue with an attempt spent.
        if (job.Attempts + 1 > MaxAttempts)
        {
            await EndAsync(db, job.Id, VideoTrimStatus.Kept, null, "too many attempts", ct);
            return true;
        }

        string? dir = null;
        try
        {
            if (!tools.HasRoomFor(job.OriginalBytes * 2))
            {
                await PostponeForRoomAsync(db, job, now, ct);
                return true;
            }
            dir = tools.NewWorkDir();
            await TrimAsync(db, storage, job, dir, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // An orderly stop isn't the file's fault, so it doesn't cost an attempt. A deploy must not use them up.
            await db.VideoTrimJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, VideoTrimStatus.Waiting)
                .SetProperty(j => j.Attempts, j => j.Attempts - 1), CancellationToken.None);
            throw;
        }
        catch (Exception e)
        {
            // Storage or the database, not the file. The video is untouched; go again later, then let it be.
            logger.LogError(e, "Trimming video {PhotoId} failed, attempt {Attempt}.", job.Id, job.Attempts + 1);
            if (job.Attempts + 1 >= MaxAttempts)
            {
                await EndAsync(db, job.Id, VideoTrimStatus.Kept, null, "kept after errors", CancellationToken.None);
            }
            else
            {
                var next = DateTimeOffset.UtcNow.AddMinutes(5 * (job.Attempts + 1));
                await db.VideoTrimJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, VideoTrimStatus.Waiting)
                    .SetProperty(j => j.NextAttemptAt, next)
                    .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), CancellationToken.None);
            }
        }
        finally
        {
            if (dir is not null) RemoveFolder(dir);
        }
        return true;
    }

    private async Task PostponeForRoomAsync(AppDbContext db, VideoTrimJob job, DateTimeOffset now, CancellationToken ct)
    {
        if (now - job.CreatedAt > GiveUpOnRoomAfter)
        {
            await EndAsync(db, job.Id, VideoTrimStatus.Kept, null, "no disk room", ct);
            return;
        }
        logger.LogWarning("No disk room to trim video {PhotoId}; waiting.", job.Id);
        // Not the file's fault, so no attempt is spent on it.
        await db.VideoTrimJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, VideoTrimStatus.Waiting)
            .SetProperty(j => j.Attempts, j => j.Attempts - 1)
            .SetProperty(j => j.NextAttemptAt, now + NoRoomWait), ct);
    }

    private async Task TrimAsync(AppDbContext db, IStorageService storage, VideoTrimJob job, string dir, CancellationToken ct)
    {
        var photo = await db.EventPhotos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.Id, ct);
        if (photo is not { DeletedAt: null } || storage.KeyFor(photo.Url) is not { } oldKey)
        {
            await EndAsync(db, job.Id, VideoTrimStatus.Gone, null, "photo gone", ct);
            return;
        }

        var input = Path.Combine(dir, "in");
        if (!await storage.DownloadToFileAsync(oldKey, input, ct))
        {
            await EndAsync(db, job.Id, VideoTrimStatus.Gone, null, "file gone", ct);
            return;
        }
        var oldBytes = new FileInfo(input).Length;

        var kind = await VideoSniffer.DetectFileAsync(input, ct);
        var probe = kind is null ? null : await tools.ProbeAsync(input, kind, ct);
        if (kind is null || probe is null)
        {
            await EndAsync(db, job.Id, VideoTrimStatus.Kept, null, "unreadable", ct);
            return;
        }

        var why = VideoTrimRules.WhyNotCompress(probe, oldBytes);
        var smaller = Path.Combine(dir, "out.mp4");
        if (why is null)
        {
            why = await CompressAsync(input, smaller, kind, probe, oldBytes, ct);
            if (why is null)
            {
                var stem = Path.ChangeExtension(oldKey, null);
                var trimProbe = await tools.ProbeAsync(smaller, Mp4Kind, ct);
                await SwapAsync(db, storage, job, photo, oldKey, oldBytes, smaller, $"{stem}_v.mp4", "video/mp4",
                    trimProbe!, VideoTrimStatus.Trimmed, $"h264 from {oldBytes / MediaLimits.Mb} MB", ct);
                return;
            }
        }

        // Not compressed, for the reason in `why`. What's left is the metadata, if it's still there.
        if (job.MetadataClean)
        {
            await EndAsync(db, job.Id, VideoTrimStatus.Kept, oldBytes, why, ct);
            return;
        }
        var stripped = Path.Combine(dir, "clean");
        if (!await tools.StripMetadataAsync(input, stripped, kind, ct))
        {
            logger.LogWarning("Video {PhotoId} keeps its metadata: it couldn't be removed.", job.Id);
            await EndAsync(db, job.Id, VideoTrimStatus.Kept, oldBytes, "metadata stayed", ct);
            return;
        }
        var strippedProbe = await tools.ProbeAsync(stripped, kind, ct);
        if (strippedProbe is null)
        {
            await EndAsync(db, job.Id, VideoTrimStatus.Kept, oldBytes, "metadata stayed", ct);
            return;
        }
        await SwapAsync(db, storage, job, photo, oldKey, oldBytes, stripped,
            $"{Path.ChangeExtension(oldKey, null)}_s{kind.Extension}", kind.ContentType,
            strippedProbe, VideoTrimStatus.Kept, why, ct);
    }

    private static readonly Application.Common.VideoKind Mp4Kind = new("video/mp4", ".mp4", "mov", "mp4");

    /// <summary>Null when <paramref name="output"/> is a good, smaller copy; otherwise why it isn't.</summary>
    private async Task<string?> CompressAsync(
        string input, string output, VideoKind kind, VideoProbe original, long oldBytes, CancellationToken ct)
    {
        var outcome = await tools.CompressAsync(input, output, kind, ct);
        if (outcome == VideoCompressOutcome.TimedOut) return "time limit";
        if (outcome != VideoCompressOutcome.Done || !File.Exists(output)) return "ffmpeg failed";
        var newBytes = new FileInfo(output).Length;
        if (!VideoTrimRules.WorthKeeping(oldBytes, newBytes)) return "not smaller";

        var made = await tools.ProbeAsync(output, Mp4Kind, ct);
        if (made is null || !VideoTrimRules.Matches(original, made)) return "copy didn't match";
        return await tools.PlaysAsync(output, Mp4Kind, made.Seconds, ct) ? null : "copy didn't play";
    }

    /// <summary>
    /// The new file goes up under its own name first. The row is locked for the decision, so a delete
    /// either finished first (and the new file comes back out) or waits and then sees the new file
    /// (see EventPhotoService.RemoveAsync).
    /// </summary>
    private async Task SwapAsync(
        AppDbContext db, IStorageService storage, VideoTrimJob job, EventPhoto seen, string oldKey, long oldBytes,
        string file, string newKey, string contentType, VideoProbe probe, VideoTrimStatus status, string note,
        CancellationToken ct)
    {
        var url = await storage.PutFileAsync(newKey, file, contentType, ct);
        var newBytes = new FileInfo(file).Length;
        var now = DateTimeOffset.UtcNow;

        bool swapped;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var photo = await db.EventPhotos
                .FromSqlInterpolated($"SELECT * FROM event_photos WHERE id = {job.Id} FOR UPDATE")
                .FirstOrDefaultAsync(ct);
            swapped = photo is { DeletedAt: null } && photo.Url == seen.Url;
            if (swapped)
            {
                photo!.Url = url;
                photo.OriginalUrl = url;
                photo.ContentType = contentType;
                photo.Width = probe.Width;
                photo.Height = probe.Height;
                photo.SizeBytes += newBytes - oldBytes;

                db.PendingRemovals.Add(new PendingRemoval
                {
                    Id = Guid.NewGuid(),
                    Kind = PendingRemovalKinds.File,
                    Target = oldKey,
                    NextAttemptAt = job.MetadataClean ? now + OldFileGrace : now,
                    CreatedAt = now,
                });
                await db.SaveChangesAsync(ct);

                if (photo.BucketId is { } bucket)
                    await db.MediaBuckets.Where(b => b.Id == bucket).ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.UsedBytes, b => b.UsedBytes + (newBytes - oldBytes) < 0 ? 0 : b.UsedBytes + (newBytes - oldBytes))
                        .SetProperty(b => b.UpdatedAt, now), ct);
                await EndAsync(db, job.Id, status, newBytes, note, ct);
                await tx.CommitAsync(ct);
            }
        }

        if (swapped)
        {
            removals.Wake();
            logger.LogInformation("Video {PhotoId}: {Old} bytes became {New} ({Note}).", job.Id, oldBytes, newBytes, note);
            return;
        }

        // Deleted while the work was going on, by its owner or with an account. Nothing may stay of it.
        db.ChangeTracker.Clear();
        await storage.DeleteAsync(newKey, CancellationToken.None);
        await EndAsync(db, job.Id, VideoTrimStatus.Gone, null, "photo gone", CancellationToken.None);
    }

    /// <summary>By update, not a tracked save: the row is gone already when the photo was removed outright.</summary>
    private static Task EndAsync(AppDbContext db, Guid id, VideoTrimStatus status, long? bytes, string note, CancellationToken ct) =>
        db.VideoTrimJobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, status)
            .SetProperty(j => j.ResultBytes, bytes)
            .SetProperty(j => j.Note, note)
            .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);

    private void RemoveFolder(string dir)
    {
        try
        {
            Directory.Delete(dir, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(e, "Couldn't remove a video working folder.");
        }
    }
}
