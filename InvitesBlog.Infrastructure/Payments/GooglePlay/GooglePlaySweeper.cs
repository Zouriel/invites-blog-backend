using InvitesBlog.Application.Services.Billing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Payments.GooglePlay;

/// <summary>
/// Google Play's background work, in the API host. Every five minutes, what was applied but not yet
/// consumed at Google is tried again. Every six hours, subscriptions near their end are re-checked, and the
/// voided list and missing amounts are read: what still works if no notification ever arrives.
/// Does nothing while Google Play isn't configured.
/// </summary>
public sealed class GooglePlaySweeper(IServiceProvider services, ILogger<GooglePlaySweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FullEvery = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken);
        var lastFull = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = DateTimeOffset.UtcNow;
            var full = now - lastFull >= FullEvery;
            try
            {
                using var scope = services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IGooglePlayPurchases>().SweepAsync(now, full, stoppingToken);
                if (full) lastFull = now;
            }
            catch (GooglePlayUnavailableException)
            {
                logger.LogWarning("Google Play sweep skipped: Google isn't answering.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Google Play sweep failed.");
            }
            await Task.Delay(Every, stoppingToken);
        }
    }
}
