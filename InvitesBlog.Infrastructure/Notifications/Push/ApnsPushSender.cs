using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Notifications.Push;

/// <summary>
/// Sends to an account's iPhones and iPads straight through APNs, one request per device, each to the
/// environment its token came from, and forgets devices Apple says are gone. Logs name a device by its
/// row id, never its token.
/// </summary>
public sealed class ApnsPushSender(
    ApnsClient apns,
    IRepository<DeviceToken> devices,
    IUnitOfWork uow,
    ILogger<ApnsPushSender> logger) : IPushSender
{
    public async Task SendAsync(
        Guid userId, string title, string body, IReadOnlyDictionary<string, string>? data, CancellationToken ct)
    {
        if (!apns.IsConfigured) return;
        var mine = await devices.Query(tracking: true)
            .Where(d => d.UserId == userId && d.Platform == DeviceToken.Ios)
            .ToListAsync(ct);
        if (mine.Count == 0) return;

        var payload = ApnsClient.Payload(title, body, data);
        var gone = new List<DeviceToken>();
        foreach (var device in mine)
        {
            try
            {
                var outcome = await apns.SendAsync(device.Token, device.Environment == DeviceToken.Sandbox, payload, ct);
                if (outcome == ApnsOutcome.Gone) gone.Add(device);
                else if (outcome != ApnsOutcome.Sent)
                    logger.LogInformation("Push to device {DeviceId} not delivered: {Outcome}.", device.Id, outcome);
            }
            // One device failing must not stop the others.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("Push to device {DeviceId} failed: {Error}.", device.Id, ex.GetType().Name);
            }
        }

        if (gone.Count == 0) return;
        devices.RemoveRange(gone);
        try
        {
            await uow.SaveChangesAsync(ct);
            logger.LogInformation("Forgot {Count} device(s) Apple reported as gone.", gone.Count);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("A device Apple reported as gone had already been removed.");
        }
    }
}

/// <summary>Every configured push service in turn; each one sends only to its own platform's devices.</summary>
public sealed class CompositePushSender(IReadOnlyList<IPushSender> senders, ILogger<CompositePushSender> logger) : IPushSender
{
    public async Task SendAsync(
        Guid userId, string title, string body, IReadOnlyDictionary<string, string>? data, CancellationToken ct)
    {
        foreach (var sender in senders)
        {
            try
            {
                await sender.SendAsync(userId, title, body, data, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning("A push service failed: {Error}.", ex.GetType().Name);
            }
        }
    }
}
