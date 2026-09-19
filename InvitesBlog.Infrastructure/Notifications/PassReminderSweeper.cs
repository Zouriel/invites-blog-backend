using InvitesBlog.Application.Plans;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Notifications;

/// <summary>Runs <see cref="IPassReminderService"/> every few hours, in the API host.</summary>
public sealed class PassReminderSweeper(IServiceProvider services, ILogger<PassReminderSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var sent = await scope.ServiceProvider.GetRequiredService<IPassReminderService>().RunOnceAsync(DateTimeOffset.UtcNow, stoppingToken);
                if (sent > 0) logger.LogInformation("Pass reminders sent: {Count}.", sent);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Pass reminder sweep failed.");
            }
            await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
        }
    }
}
