using InvitesBlog.Application.Plans;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Notifications;

/// <summary>
/// Keeps the Designer role in step with Studio (see <see cref="IDesignerAccessService"/>): shortly
/// after start, then hourly, so a Studio plan that runs out takes the designer with it. In the API
/// host, like every background job here — there is no separate worker in production.
/// </summary>
public sealed class DesignerAccessSweeper(IServiceProvider services, ILogger<DesignerAccessSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = services.CreateScope();
                var changed = await scope.ServiceProvider.GetRequiredService<IDesignerAccessService>().SweepAsync(stoppingToken);
                if (changed > 0) logger.LogInformation("Designer access follows Studio: {Count} account(s) updated.", changed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Designer access sweep failed.");
            }
            await Task.Delay(TimeSpan.FromHours(1), stoppingToken);
        }
    }
}
