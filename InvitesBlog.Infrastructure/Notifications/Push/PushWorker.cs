using System.Threading.Channels;
using InvitesBlog.Application.Push;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Notifications.Push;

/// <summary>
/// The in-memory line of push jobs. Bounded, and a full line drops the newest job rather than wait:
/// a lost notification is better than a slow reply to whoever caused it. Jobs waiting at a restart are
/// lost too, which is acceptable for notifications that mirror what the inbox and dashboard show anyway.
/// </summary>
public sealed class PushQueue(ILogger<PushQueue> logger) : IPushQueue
{
    public const int Capacity = 2000;

    private readonly Channel<PushJob> _jobs = Channel.CreateBounded<PushJob>(new BoundedChannelOptions(Capacity)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
    });

    public ChannelReader<PushJob> Reader => _jobs.Reader;

    public void Enqueue(PushJob job)
    {
        if (!_jobs.Writer.TryWrite(job))
            logger.LogWarning("Push line is full; a {Job} notification was dropped.", job.GetType().Name);
    }
}

/// <summary>
/// Takes jobs off the <see cref="PushQueue"/> one at a time, each in a scope of its own, so a slow push
/// service or a failure never reaches the request that raised the job. Registered in the API host.
/// </summary>
public sealed class PushWorker(PushQueue queue, IServiceScopeFactory scopes, ILogger<PushWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await foreach (var job in queue.Reader.ReadAllAsync(stoppingToken))
                await RunAsync(job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
    }

    /// <summary>Everything waiting, now. Tests use it in place of the running worker.</summary>
    public async Task<int> DrainAsync(CancellationToken ct = default)
    {
        var n = 0;
        while (queue.Reader.TryRead(out var job))
        {
            await RunAsync(job, ct);
            n++;
        }
        return n;
    }

    private async Task RunAsync(PushJob job, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            await scope.ServiceProvider.GetRequiredService<PushNotificationService>().HandleAsync(job, ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // The kind of job and the error's type: messages from deep down can carry addresses or tokens.
            logger.LogError("Push for {Job} failed: {Error}.", job.GetType().Name, ex.GetType().Name);
        }
    }
}
