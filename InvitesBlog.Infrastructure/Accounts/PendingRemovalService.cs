using InvitesBlog.Application.Abstractions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Accounts;

/// <summary>Wakes <see cref="PendingRemovalService"/> when a deletion has just committed.</summary>
public sealed class PendingRemovalSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);

    public void Wake()
    {
        if (_wake.CurrentCount == 0)
            try { _wake.Release(); } catch (SemaphoreFullException) { /* already awake */ }
    }

    public Task WaitAsync(TimeSpan timeout, CancellationToken ct) => _wake.WaitAsync(timeout, ct);
}

/// <summary>
/// Works through <see cref="PendingRemoval"/>. In the API host because there is no worker in production.
/// A failure is logged and retried later with a growing wait; the account's deletion already stands. Runs
/// on one API replica, as the other sweeps do.
/// </summary>
public sealed class PendingRemovalService(
    IServiceProvider services,
    PendingRemovalSignal signal,
    ILogger<PendingRemovalService> logger) : BackgroundService
{
    private const int Batch = 200;
    /// <summary>About ten days at the longest wait, then it is one for a person to look at.</summary>
    public const int MaxAttempts = 40;
    private static readonly TimeSpan MaxWait = TimeSpan.FromHours(6);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting (and migrating) first.
        await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                while (await RunOnceAsync(stoppingToken) > 0) { }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "The pending removals sweep failed.");
            }
            await signal.WaitAsync(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }

    /// <summary>One batch of what is due. Returns how many were taken off the list.</summary>
    public async Task<int> RunOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IStorageService>();
        var incoming = scope.ServiceProvider.GetRequiredService<IIncomingUploadStore>();
        var bank = scope.ServiceProvider.GetRequiredService<IRecurringPaymentProvider>();
        var play = scope.ServiceProvider.GetRequiredService<Application.Services.Billing.IGooglePlayPublisher>();
        var apple = scope.ServiceProvider.GetRequiredService<IAppleSignInTokens>();
        var config = scope.ServiceProvider.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();

        var now = DateTimeOffset.UtcNow;
        var due = await db.PendingRemovals.Where(r => r.NextAttemptAt <= now)
            .OrderBy(r => r.NextAttemptAt).Take(Batch).ToListAsync(ct);

        var done = 0;
        foreach (var row in due)
        {
            try
            {
                switch (row.Kind)
                {
                    case PendingRemovalKinds.File: await storage.DeleteAsync(row.Target, ct); break;
                    case PendingRemovalKinds.Prefix: await storage.DeletePrefixAsync(row.Target, ct); break;
                    case PendingRemovalKinds.IncomingFile:
                        if (incoming.Available) await incoming.DeleteAsync(row.Target, ct);
                        break;
                    case PendingRemovalKinds.PaymentCustomer: await bank.ForgetCustomerAsync(row.Target, ct); break;
                    // False means Google refused it, which it does for one already ended: nothing to stop.
                    case PendingRemovalKinds.PlaySubscription: await play.CancelSubscriptionAsync(row.Target, ct); break;
                    case PendingRemovalKinds.AppleSignIn:
                        // Unreadable means the server's secret changed since: nothing left to revoke with.
                        // Revoked as the client it was issued to (the app, or the website's Services ID).
                        if (Application.Common.AppleTokenSeal.OpenWithClient(row.Target, config) is { } refresh)
                            await apple.RevokeAsync(refresh.Token, refresh.ClientId, ct);
                        else
                            logger.LogWarning("Removal {RemovalId}: the Apple token couldn't be unsealed; dropped.", row.Id);
                        break;
                    default: throw new InvalidOperationException($"Unknown removal kind {row.Kind}.");
                }
                db.PendingRemovals.Remove(row);
                done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                row.Attempts++;
                if (row.Attempts >= MaxAttempts)
                {
                    // Left for a person: nothing here will fix it, and the row says what remains.
                    logger.LogError(ex, "Giving up on removal {RemovalId} ({Kind}) after {Attempts} tries.", row.Id, row.Kind, row.Attempts);
                    row.NextAttemptAt = now.AddYears(100);
                }
                else
                {
                    logger.LogWarning(ex, "Removal {RemovalId} ({Kind}) failed, try {Attempts}; will retry.", row.Id, row.Kind, row.Attempts);
                    var wait = TimeSpan.FromMinutes(Math.Pow(2, Math.Min(row.Attempts, 10)));
                    row.NextAttemptAt = now + (wait < MaxWait ? wait : MaxWait);
                }
            }
        }
        await db.SaveChangesAsync(ct);
        return done;
    }
}
