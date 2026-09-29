using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Application.Services.Payments;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Payments;

/// <summary>
/// The payments' background work, in the API host.
///
/// <para><b>Every two minutes</b>, payments still pending at the gateway are asked about
/// (<see cref="IPaymentService.SyncAsync"/>). The webhook is the primary way a result arrives; this is
/// the safety net for one that was lost, refused, or never sent, so a buyer who paid is never left
/// without what they paid for. A payment left pending past the gateway's own expiry is closed as failed.</para>
///
/// <para><b>Every hour</b>, plans due to renew are renewed with their saved cards
/// (<see cref="ISubscriptionRenewalService"/>).</para>
///
/// <para>Nothing runs while <c>Payments:Enabled</c> is off.</para>
/// </summary>
public sealed class PaymentSweeper(IServiceProvider services, IConfiguration config, ILogger<PaymentSweeper> logger) : BackgroundService
{
    private static readonly TimeSpan SyncEvery = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan RenewEvery = TimeSpan.FromHours(1);
    /// <summary>Not asked about until it's had a minute: the buyer is probably still on the gateway's page.</summary>
    private static readonly TimeSpan SyncAfter = TimeSpan.FromMinutes(1);
    /// <summary>BML's payment links expire after a week; a day more and it is certainly over.</summary>
    private static readonly TimeSpan ExpireAfter = TimeSpan.FromDays(8);

    private bool Enabled => bool.TryParse(config["Payments:Enabled"], out var on) && on;
    private DateTimeOffset _lastRenewal = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            if (Enabled)
            {
                await Guard("Payment status sweep", () => SyncPendingAsync(DateTimeOffset.UtcNow, stoppingToken));
                if (DateTimeOffset.UtcNow - _lastRenewal >= RenewEvery)
                {
                    _lastRenewal = DateTimeOffset.UtcNow;
                    await Guard("Renewal sweep", () => RenewAsync(stoppingToken));
                }
            }
            await Task.Delay(SyncEvery, stoppingToken);
        }
    }

    public async Task SyncPendingAsync(DateTimeOffset now, CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var provider = scope.ServiceProvider.GetRequiredService<IPaymentProvider>();
        var payments = scope.ServiceProvider.GetRequiredService<IPaymentService>();
        var outcomes = scope.ServiceProvider.GetRequiredService<PaymentOutcomes>();

        var expired = now - ExpireAfter;
        var stale = await db.Payments
            .Where(p => p.Provider == provider.Name && p.Status == PaymentStatus.Pending && p.CreatedAt < expired)
            .ToListAsync(ct);
        foreach (var p in stale) p.Status = PaymentStatus.Failed;
        if (stale.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Closed {Count} payments left pending past the gateway's expiry.", stale.Count);
        }

        var settledBefore = now - SyncAfter;
        var pending = await db.Payments.AsNoTracking()
            .Where(p => p.Provider == provider.Name && p.ProviderSessionId != null
                        && (p.Status == PaymentStatus.Pending || p.Status == PaymentStatus.Created)
                        && p.CreatedAt < settledBefore && p.CreatedAt >= expired)
            .OrderBy(p => p.CreatedAt)
            .Select(p => p.Id)
            .Take(200)
            .ToListAsync(ct);

        foreach (var id in pending)
        {
            try
            {
                var result = await payments.SyncAsync(id, ct);
                await outcomes.ApplyAsync(result);
                if (result.FulfilPaymentId is not null || result.DispatchCampaignId is not null)
                    logger.LogInformation("Payment {PaymentId} confirmed by the status sweep (its webhook hadn't).", id);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "Couldn't check payment {PaymentId} with the gateway.", id);
            }
        }
    }

    private async Task RenewAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var tried = await scope.ServiceProvider.GetRequiredService<ISubscriptionRenewalService>()
            .RunOnceAsync(DateTimeOffset.UtcNow, ct);
        if (tried > 0) logger.LogInformation("Renewals tried: {Count}.", tried);
    }

    private async Task Guard(string what, Func<Task> run)
    {
        try { await run(); }
        catch (Exception ex) when (ex is not OperationCanceledException) { logger.LogError(ex, "{What} failed.", what); }
    }
}
