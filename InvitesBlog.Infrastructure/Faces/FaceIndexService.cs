using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Faces;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Faces;

/// <summary>
/// Finds the faces in queued photos (<see cref="FaceJob"/>) and puts each in a group, one photo at a time, in the
/// API host (there is no worker in production). The models load for a run of work and are released a minute
/// after the last photo (<see cref="FaceAnalyzer"/>), so an idle server holds none of their memory.
///
/// <para>One worker, so groups are only ever changed here, in order, and need no locking. Everything for a photo
/// is saved in one transaction with its job's removal. Before saving, the album and photo are checked again:
/// a photo removed, or grouping switched off, while it was being read leaves nothing behind.</para>
/// </summary>
public sealed class FaceIndexService(
    IServiceProvider services,
    IFaceAnalyzer analyzer,
    IConfiguration config,
    ILogger<FaceIndexService> logger) : BackgroundService
{
    public const int MaxAttempts = 3;
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(5);

    private float Threshold => float.TryParse(config["Faces:MatchThreshold"], System.Globalization.CultureInfo.InvariantCulture, out var v)
        ? v
        : FaceClustering.DefaultThreshold;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!string.Equals(config["Faces:Index"] ?? "on", "on", StringComparison.OrdinalIgnoreCase)) return;
        if (!analyzer.Available) return;

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
                logger.LogError(ex, "The face grouping pass failed.");
            }
            if (!worked) await Task.Delay(Idle, stoppingToken);
        }
    }

    /// <summary>At startup: a photo that was mid-way goes back in the queue (its attempt stays counted).</summary>
    public async Task ReleaseInterruptedAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.FaceJobs.Where(j => j.Status == FaceJobStatus.Running)
            .ExecuteUpdateAsync(s => s.SetProperty(j => j.Status, FaceJobStatus.Waiting), ct);
    }

    /// <summary>One photo if one is due. False when there was nothing to do.</summary>
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IStorageService>();

        var now = DateTimeOffset.UtcNow;
        var job = await db.FaceJobs.AsNoTracking()
            .Where(j => j.Status == FaceJobStatus.Waiting && j.NextAttemptAt <= now)
            .OrderBy(j => j.CreatedAt).FirstOrDefaultAsync(ct);
        if (job is null) return false;

        var claimed = await db.FaceJobs.Where(j => j.Id == job.Id && j.Status == FaceJobStatus.Waiting)
            .ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, FaceJobStatus.Running)
                .SetProperty(j => j.Attempts, j => j.Attempts + 1)
                .SetProperty(j => j.UpdatedAt, now), ct);
        if (claimed == 0) return true;

        if (job.Attempts + 1 > MaxAttempts)
        {
            await FailAsync(db, job.Id, "too many attempts", ct);
            return true;
        }

        try
        {
            await IndexAsync(db, storage, job, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await db.FaceJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
                .SetProperty(j => j.Status, FaceJobStatus.Waiting)
                .SetProperty(j => j.Attempts, j => j.Attempts - 1), CancellationToken.None);
            throw;
        }
        catch (BusinessRuleException e)
        {
            // The picture itself can't be read: trying again won't help.
            await FailAsync(db, job.Id, e.Message, CancellationToken.None);
        }
        catch (Exception e)
        {
            logger.LogError(e, "Finding faces in photo {PhotoId} failed, attempt {Attempt}.", job.Id, job.Attempts + 1);
            if (job.Attempts + 1 >= MaxAttempts)
            {
                await FailAsync(db, job.Id, "failed after errors", CancellationToken.None);
            }
            else
            {
                var next = DateTimeOffset.UtcNow.AddMinutes(2 * (job.Attempts + 1));
                await db.FaceJobs.Where(j => j.Id == job.Id).ExecuteUpdateAsync(s => s
                    .SetProperty(j => j.Status, FaceJobStatus.Waiting)
                    .SetProperty(j => j.NextAttemptAt, next)
                    .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), CancellationToken.None);
            }
        }
        return true;
    }

    private static Task FailAsync(AppDbContext db, Guid id, string note, CancellationToken ct) =>
        db.FaceJobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s
            .SetProperty(j => j.Status, FaceJobStatus.Failed)
            .SetProperty(j => j.Note, note.Length > 200 ? note[..200] : note)
            .SetProperty(j => j.UpdatedAt, DateTimeOffset.UtcNow), ct);

    private async Task IndexAsync(AppDbContext db, IStorageService storage, FaceJob job, CancellationToken ct)
    {
        var photo = await db.EventPhotos.AsNoTracking().FirstOrDefaultAsync(p => p.Id == job.Id, ct);
        var on = await db.MediaBuckets.AnyAsync(b => b.Id == job.BucketId && b.FaceGroupingOn, ct);
        if (photo is null || photo.DeletedAt is not null || !on || !FaceGroupingService.IsPicture(photo))
        {
            await db.FaceJobs.Where(j => j.Id == job.Id).ExecuteDeleteAsync(ct);
            return;
        }

        // The viewing copy (upright, at most 2048 px): plenty for faces, and much smaller than the original.
        var key = storage.KeyFor(photo.Url) ?? throw new InvalidOperationException("The photo has no stored file.");
        var bytes = await storage.GetAsync(key, ct) ?? throw new InvalidOperationException("The photo's file is missing.");
        var found = await analyzer.AnalyzeAsync(bytes, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // Again, inside the transaction: grouping switched off or the photo removed meanwhile leaves nothing.
        var stillOn = await db.MediaBuckets.AnyAsync(b => b.Id == job.BucketId && b.FaceGroupingOn, ct);
        var stillThere = await db.EventPhotos.AnyAsync(p => p.Id == job.Id && p.DeletedAt == null, ct);
        if (stillOn && stillThere)
        {
            var groups = await db.FacePeople.Where(p => p.BucketId == job.BucketId).ToListAsync(ct);
            var now = DateTimeOffset.UtcNow;
            // Run again for a photo (a retry after a crash between saves can't happen, but be sure): start clean.
            await db.PhotoFaces.Where(f => f.PhotoId == job.Id).ExecuteDeleteAsync(ct);
            foreach (var face in found)
            {
                var group = FaceClustering.Match(face.Embedding, groups, Threshold);
                if (group is { Ignored: true }) continue; // They opted out: their face isn't kept.
                if (group is null)
                {
                    group = new FacePerson { Id = Guid.NewGuid(), BucketId = job.BucketId, CreatedAt = now, UpdatedAt = now };
                    groups.Add(group);
                    db.FacePeople.Add(group);
                }
                FaceClustering.Add(group, face.Embedding);
                group.UpdatedAt = now;
                db.PhotoFaces.Add(new PhotoFace
                {
                    Id = Guid.NewGuid(),
                    BucketId = job.BucketId,
                    PhotoId = job.Id,
                    PersonId = group.Id,
                    X = face.X, Y = face.Y, W = face.W, H = face.H,
                    Score = face.Score,
                    Embedding = face.Embedding,
                    CreatedAt = now,
                });
            }
            await db.SaveChangesAsync(ct);
        }
        await db.FaceJobs.Where(j => j.Id == job.Id).ExecuteDeleteAsync(ct);
        await tx.CommitAsync(ct);

        // Someone may have opted out, switched grouping off or removed the photo while this was saving (the
        // checks above can't see a change that commits after them). Sweep after the commit, so nothing they
        // asked to be rid of outlives it by more than a moment.
        if (!await db.MediaBuckets.AnyAsync(b => b.Id == job.BucketId && b.FaceGroupingOn, CancellationToken.None))
        {
            await db.PhotoFaces.Where(f => f.BucketId == job.BucketId).ExecuteDeleteAsync(CancellationToken.None);
            await db.FacePeople.Where(p => p.BucketId == job.BucketId).ExecuteDeleteAsync(CancellationToken.None);
        }
        else
        {
            await db.PhotoFaces.Where(f => f.PhotoId == job.Id &&
                    (db.FacePeople.Any(p => p.Id == f.PersonId && p.Ignored) ||
                     db.EventPhotos.Any(p => p.Id == f.PhotoId && p.DeletedAt != null)))
                .ExecuteDeleteAsync(CancellationToken.None);
        }
        logger.LogInformation("Found {Count} faces in photo {PhotoId}", found.Count, job.Id);
    }
}
