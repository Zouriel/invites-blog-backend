using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Worker;

/// <summary>
/// Retention auto-delete (§15.4): guest data is purged <c>RetentionDays</c> after the event date
/// (default 90). Delivery logs keep only hashed addresses afterward. Runs daily.
/// </summary>
public sealed class RetentionCleanupService(IServiceProvider services, ILogger<RetentionCleanupService> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Retention cleanup failed.");
            }
            await Task.Delay(TimeSpan.FromHours(24), stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var now = DateTimeOffset.UtcNow;

        var expired = await db.Campaigns
            .Where(c => c.EventStartAt.AddDays(c.RetentionDays) < now)
            .Select(c => c.Id)
            .ToListAsync(ct);

        var purged = 0;
        foreach (var campaignId in expired)
        {
            var guests = await db.Guests.Where(g => g.CampaignId == campaignId && !g.OptedOut).ToListAsync(ct);
            foreach (var g in guests)
            {
                g.Email = null; g.PhoneE164 = null; g.PhoneRaw = null;
                g.Name = "Removed guest"; g.MetadataJson = "{}"; g.OptedOut = true;
                purged++;
            }
        }
        if (purged > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Retention: anonymized {Count} guests across {Campaigns} campaigns.", purged, expired.Count);
        }
    }
}
