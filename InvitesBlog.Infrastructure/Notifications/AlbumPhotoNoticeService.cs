using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Moderation;
using InvitesBlog.Application.Push;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Notifications;

/// <summary>
/// Tells the people who run an album (its owner, the event's organisers and the people it is for) that guests
/// added photos: one push per album per <see cref="Quiet"/> at most, covering everything since the last one, and
/// only once uploads have paused for <see cref="Settle"/> (or the oldest has waited <see cref="MaxWait"/>), so a
/// guest sending forty photos is one notification, not forty, and not "1 new photo" followed by "39".
///
/// <para>Nobody hears about their own uploads or about anyone they blocked. Guests aren't told (who may see an
/// album changes with its phase and settings; a later step). Registered in the API host only, in exactly one
/// host so two sweeps never race one album. On unless <c>Notifications:AlbumPhotos:Enabled</c> is false; each
/// person can mute it in the app (kind <see cref="PushKinds.Photos"/>).</para>
/// </summary>
public sealed class AlbumPhotoNoticeService(
    IServiceProvider services,
    IConfiguration config,
    ILogger<AlbumPhotoNoticeService> logger) : BackgroundService
{
    public static readonly TimeSpan Quiet = TimeSpan.FromHours(2);
    public static readonly TimeSpan Settle = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan MaxWait = TimeSpan.FromHours(1);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(3);

    private bool Enabled => config.GetValue("Notifications:AlbumPhotos:Enabled", true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Enabled)
                {
                    using var scope = services.CreateScope();
                    var sp = scope.ServiceProvider;
                    await SweepAsync(sp.GetRequiredService<AppDbContext>(),
                        sp.GetRequiredService<PushNotificationService>(),
                        sp.GetRequiredService<IPushSender>(),
                        sp.GetService<IModerationViewService>(),
                        DateTimeOffset.UtcNow, stoppingToken);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Album photo notice sweep failed.");
            }
            await Task.Delay(SweepEvery, stoppingToken);
        }
    }

    public sealed record Upload(Guid PhotoId, Guid? UserId, Guid? GuestId, string? Name, string ContentType, DateTimeOffset CreatedAt);

    /// <summary>One sweep at <paramref name="now"/>. Returns how many notifications went out.</summary>
    public static async Task<int> SweepAsync(
        AppDbContext db, PushNotificationService people, IPushSender sender,
        IModerationViewService? moderation, DateTimeOffset now, CancellationToken ct)
    {
        var quietSince = now - Quiet;
        var due = await db.MediaBuckets
            .Where(b => b.PhotosPushedAt == null || b.PhotosPushedAt <= quietSince)
            .Where(b => db.EventPhotos.Any(p => p.BucketId == b.Id && p.DeletedAt == null && p.CreatedAt <= now
                && p.CreatedAt > (b.PhotosPushedUpTo ?? b.CreatedAt)))
            .ToListAsync(ct);

        var sent = 0;
        foreach (var bucket in due)
        {
            var since = bucket.PhotosPushedUpTo ?? bucket.CreatedAt;
            var fresh = await db.EventPhotos
                .Where(p => p.BucketId == bucket.Id && p.DeletedAt == null && p.CreatedAt <= now && p.CreatedAt > since)
                .Select(p => new Upload(p.Id, p.UploaderUserId, p.GuestId, p.UploaderName, p.ContentType, p.CreatedAt))
                .ToListAsync(ct);
            if (fresh.Count == 0) continue;
            // Still arriving: wait for a pause, unless the first of them has waited long enough.
            if (now - fresh.Max(u => u.CreatedAt) < Settle && now - fresh.Min(u => u.CreatedAt) < MaxWait) continue;

            var userIds = fresh.Select(u => u.UserId).OfType<Guid>().Distinct().ToList();
            var names = await db.Users.Where(u => userIds.Contains(u.Id))
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
            var title = await people.AlbumTitleAsync(bucket, ct);

            foreach (var recipient in await people.AlbumPeopleAsync(bucket, ct))
            {
                var theirs = new List<Upload>();
                foreach (var u in fresh)
                {
                    if (u.UserId == recipient) continue;
                    if (u.UserId is { } by && moderation is not null && await moderation.HasBlockedAsync(recipient, by, ct)) continue;
                    theirs.Add(u);
                }
                if (theirs.Count == 0) continue;
                await sender.SendAsync(recipient, title, Body(theirs, names),
                    PushKinds.Data($"/buckets/{bucket.Id}", PushKinds.Photos, bucket.Id.ToString(), $"photos-{bucket.Id:N}"), ct);
                sent++;
            }

            var upTo = fresh.Max(u => u.CreatedAt);
            await db.MediaBuckets.Where(b => b.Id == bucket.Id).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.PhotosPushedAt, now)
                .SetProperty(b => b.PhotosPushedUpTo, upTo), ct);
        }
        return sent;
    }

    /// <summary>"Aisha added 3 photos." / "Aisha and 2 others added 14 photos and videos."</summary>
    public static string Body(IReadOnlyList<Upload> uploads, IReadOnlyDictionary<Guid, string> names)
    {
        // Newest first: the name people see is whoever added most recently.
        var people = uploads.OrderByDescending(u => u.CreatedAt)
            .Select(u => (Key: u.UserId?.ToString() ?? u.GuestId?.ToString() ?? u.Name?.Trim().ToLowerInvariant() ?? "", Upload: u))
            .DistinctBy(x => x.Key).ToList();
        var newest = people[0].Upload;
        var first = newest.UserId is { } id && names.TryGetValue(id, out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim()
            : !string.IsNullOrWhiteSpace(newest.Name) ? newest.Name.Trim()
            : null;
        var videos = uploads.Count(u => u.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase));
        var what = uploads.Count == 1
            ? videos == 1 ? "a video" : "a photo"
            : videos == uploads.Count ? $"{uploads.Count} videos"
            : videos == 0 ? $"{uploads.Count} photos"
            : $"{uploads.Count} photos and videos";
        if (first is null) return people.Count == 1 ? $"A guest added {what}." : $"{people.Count} guests added {what}.";
        return people.Count switch
        {
            1 => $"{first} added {what}.",
            2 => $"{first} and 1 other added {what}.",
            _ => $"{first} and {people.Count - 1} others added {what}.",
        };
    }
}
