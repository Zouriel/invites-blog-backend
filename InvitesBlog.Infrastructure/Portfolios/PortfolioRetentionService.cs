using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Portfolios;

/// <summary>
/// Keeps a lapsed Studio's portfolio for <see cref="KeepFor"/> (docs/PORTFOLIO.md §4), then deletes it with its files.
/// The page itself goes unavailable the moment Studio ends (checked on every read); this only marks when that was
/// and does the deleting. Also clears handle redirects that have run out.
/// </summary>
public sealed class PortfolioRetentionService(IServiceProvider services, ILogger<PortfolioRetentionService> logger) : BackgroundService
{
    public static readonly TimeSpan KeepFor = TimeSpan.FromDays(90);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the API finish starting (migrations) first.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var deleted = await SweepAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(),
                    scope.ServiceProvider.GetRequiredService<IStorageService>(), DateTimeOffset.UtcNow, stoppingToken);
                if (deleted > 0) logger.LogInformation("Deleted {Count} portfolio(s) 90 days after their Studio ended", deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Portfolio retention sweep failed.");
            }
            await Task.Delay(SweepEvery, stoppingToken);
        }
    }

    /// <summary>One sweep at <paramref name="now"/>. Returns how many portfolios were deleted.</summary>
    public static async Task<int> SweepAsync(AppDbContext db, IStorageService storage, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await db.Portfolios.Join(db.Users, p => p.UserId, u => u.Id, (p, u) => new { p, u }).ToListAsync(ct);
        var deleted = 0;
        foreach (var x in rows)
        {
            if (AddOns.StudioActive(x.u, now))
            {
                x.p.StudioEndedAt = null;
                continue;
            }
            x.p.StudioEndedAt ??= now;
            if (now - x.p.StudioEndedAt.Value < KeepFor) continue;
            await storage.DeletePrefixAsync($"portfolios/{x.p.Id:N}/", ct);
            db.Portfolios.Remove(x.p);
            deleted++;
        }
        await db.SaveChangesAsync(ct);
        await db.PortfolioHandleRedirects.Where(r => r.ExpiresAt < now).ExecuteDeleteAsync(ct);
        return deleted;
    }
}
