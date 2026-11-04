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
/// Tells an album's owner, and the people its event is for, that someone hearted what is in it: one
/// push per album per <see cref="Quiet"/> at most, covering every heart since the last one, so a
/// wedding where forty guests heart two hundred photos is a few notifications, not two hundred.
///
/// <para>Nobody hears about their own hearts, or about anyone they blocked. Registered in the API host
/// only (the worker is not deployed), and in exactly one host so two sweeps never race one album.
/// On unless <c>Notifications:Favourites:Enabled</c> is false; with push not configured the sender
/// drops everything anyway.</para>
/// </summary>
public sealed class FavouriteNoticeService(
    IServiceProvider services,
    IConfiguration config,
    ILogger<FavouriteNoticeService> logger) : BackgroundService
{
    public static readonly TimeSpan Quiet = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(2);

    private bool Enabled => config.GetValue("Notifications:Favourites:Enabled", true);

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
            catch (Exception ex)
            {
                logger.LogError(ex, "Favourite notice sweep failed.");
            }
            await Task.Delay(SweepEvery, stoppingToken);
        }
    }

    /// <summary>One sweep at <paramref name="now"/>. Returns how many notifications went out.</summary>
    public static async Task<int> SweepAsync(
        AppDbContext db, PushNotificationService people, IPushSender sender,
        IModerationViewService? moderation, DateTimeOffset now, CancellationToken ct)
    {
        var quietSince = now - Quiet;
        var due = await db.MediaBuckets
            .Where(b => b.FavouritesNotifiedAt == null || b.FavouritesNotifiedAt <= quietSince)
            .Where(b => db.Favourites.Any(f => f.CreatedAt <= now
                && (b.FavouritesNotifiedUpTo == null || f.CreatedAt > b.FavouritesNotifiedUpTo)
                && db.EventPhotos.Any(p => p.Id == f.PhotoId && p.BucketId == b.Id && p.DeletedAt == null)))
            .ToListAsync(ct);

        var sent = 0;
        foreach (var bucket in due)
        {
            var hearts = await db.Favourites
                .Where(f => f.CreatedAt <= now
                    && (bucket.FavouritesNotifiedUpTo == null || f.CreatedAt > bucket.FavouritesNotifiedUpTo))
                .Join(db.EventPhotos.Where(p => p.BucketId == bucket.Id && p.DeletedAt == null),
                    f => f.PhotoId, p => p.Id,
                    (f, p) => new Heart(f.UserId, f.PhotoId, f.CreatedAt, p.ContentType))
                .ToListAsync(ct);
            if (hearts.Count == 0) continue;

            var actorIds = hearts.Select(h => h.UserId).Distinct().ToList();
            var names = await db.Users.Where(u => actorIds.Contains(u.Id) && u.IsActive)
                .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
            var title = string.IsNullOrWhiteSpace(bucket.Name) ? "Your album" : bucket.Name;
            if (bucket.CampaignId != Guid.Empty
                && await db.Campaigns.Where(c => c.Id == bucket.CampaignId).Select(c => c.Title).FirstOrDefaultAsync(ct) is { } t
                && !string.IsNullOrWhiteSpace(t))
                title = t;

            foreach (var recipient in await people.AlbumPeopleAsync(bucket, ct))
            {
                var theirs = new List<Heart>();
                foreach (var h in hearts)
                {
                    if (h.UserId == recipient || !names.ContainsKey(h.UserId)) continue;
                    if (moderation is not null && await moderation.HasBlockedAsync(recipient, h.UserId, ct)) continue;
                    theirs.Add(h);
                }
                if (theirs.Count == 0) continue;
                await sender.SendAsync(recipient, title, Body(theirs, names),
                    new Dictionary<string, string> { ["path"] = $"/buckets/{bucket.Id}" }, ct);
                sent++;
            }

            var upTo = hearts.Max(h => h.CreatedAt);
            await db.MediaBuckets.Where(b => b.Id == bucket.Id).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.FavouritesNotifiedAt, now)
                .SetProperty(b => b.FavouritesNotifiedUpTo, upTo), ct);
        }
        return sent;
    }

    public sealed record Heart(Guid UserId, Guid PhotoId, DateTimeOffset CreatedAt, string ContentType);

    /// <summary>"Aisha loved a photo." / "Aisha and 2 others loved 5 photos."</summary>
    public static string Body(IReadOnlyList<Heart> hearts, IReadOnlyDictionary<Guid, string> names)
    {
        var people = hearts.OrderByDescending(h => h.CreatedAt).Select(h => h.UserId).Distinct().ToList();
        var first = names.TryGetValue(people[0], out var n) && !string.IsNullOrWhiteSpace(n) ? n.Trim() : "Someone";
        var who = people.Count switch
        {
            1 => first,
            2 => $"{first} and 1 other",
            _ => $"{first} and {people.Count - 1} others",
        };
        // Items, not hearts: two people loving the same photo is one photo.
        var items = hearts.DistinctBy(h => h.PhotoId).ToList();
        var videos = items.Count(h => h.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase));
        var what = items.Count == 1
            ? videos == 1 ? "a video" : "a photo"
            : videos == items.Count ? $"{items.Count} videos"
            : videos == 0 ? $"{items.Count} photos"
            : $"{items.Count} photos and videos";
        return $"{who} loved {what}.";
    }
}
