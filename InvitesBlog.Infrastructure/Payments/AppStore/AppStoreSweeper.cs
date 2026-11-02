using InvitesBlog.Application.Services.Billing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Payments.AppStore;

/// <summary>
/// The App Store's background work, in the API host: every six hours, subscriptions near their end are
/// re-checked and Apple's notification history is read, so refunds and renewals are seen even if no
/// notification ever reached us. Does nothing while the App Store isn't configured.
/// </summary>
public sealed class AppStoreSweeper(IServiceProvider services, ILogger<AppStoreSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                await scope.ServiceProvider.GetRequiredService<IAppStorePurchases>().SweepAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (AppStoreUnavailableException)
            {
                logger.LogWarning("App Store sweep skipped: Apple isn't answering.");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "App Store sweep failed.");
            }
            await Task.Delay(Every, stoppingToken);
        }
    }
}
