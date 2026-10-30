using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Services.Photos;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Photos;

/// <summary>
/// Turns uploaded files into photographs, a few at a time so a big party can't take over a shared
/// server. Progress is in the database, so a restart resumes instead of losing anything.
/// Runs inside the API host: there is no separate worker in production.
/// </summary>
public sealed class DirectUploadProcessor(
    IServiceProvider services,
    IIncomingUploadStore incoming,
    IConfiguration config,
    ILogger<DirectUploadProcessor> logger) : BackgroundService
{
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!incoming.Available) return;

        var workers = Math.Clamp(config.GetValue("Uploads:Workers", 3), 1, 8);
        using var slots = new SemaphoreSlim(workers, workers);
        var running = new List<Task>();

        // Let the app finish starting (and migrating) first.
        await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken);
        await ReleaseAbandonedAsync(stoppingToken);

        var lastSweep = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (DateTimeOffset.UtcNow - lastSweep > SweepEvery)
                {
                    lastSweep = DateTimeOffset.UtcNow;
                    using var scope = services.CreateScope();
                    await scope.ServiceProvider.GetRequiredService<IDirectUploadService>().SweepAsync(stoppingToken);
                }

                running.RemoveAll(t => t.IsCompleted);
                foreach (var id in await ClaimAsync(slots.CurrentCount, stoppingToken))
                {
                    await slots.WaitAsync(stoppingToken);
                    running.Add(RunAsync(id, slots, stoppingToken));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Direct upload processor failed a pass.");
            }
            await Task.Delay(Idle, stoppingToken);
        }
    }

    /// <summary>Only one host runs this, so anything still marked Processing at start was interrupted.</summary>
    private async Task ReleaseAbandonedAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await db.PendingUploads.Where(u => u.Status == PendingUploadStatus.Processing)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.Status, PendingUploadStatus.Uploaded), ct);
    }

    private async Task<List<Guid>> ClaimAsync(int count, CancellationToken ct)
    {
        if (count == 0) return [];
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;
        var next = await db.PendingUploads
            .Where(u => u.Status == PendingUploadStatus.Uploaded && u.NextAttemptAt <= now)
            .OrderBy(u => u.CreatedAt).Take(count).Select(u => u.Id).ToListAsync(ct);

        var claimed = new List<Guid>();
        foreach (var id in next)
        {
            var moved = await db.PendingUploads
                .Where(u => u.Id == id && u.Status == PendingUploadStatus.Uploaded)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(u => u.Status, PendingUploadStatus.Processing)
                    .SetProperty(u => u.UpdatedAt, now), ct);
            if (moved == 1) claimed.Add(id);
        }
        return claimed;
    }

    private async Task RunAsync(Guid id, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            await Task.Yield();
            using var scope = services.CreateScope();
            await scope.ServiceProvider.GetRequiredService<IDirectUploadService>().FinishAsync(id, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Direct upload {UploadId} could not be finished.", id);
        }
        catch (OperationCanceledException)
        {
            // Shutting down; ReleaseAbandonedAsync picks it up on the next start.
        }
        finally
        {
            slots.Release();
        }
    }
}
