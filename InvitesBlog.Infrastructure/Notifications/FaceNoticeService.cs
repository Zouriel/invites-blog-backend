using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Events;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Push;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Notifications;

/// <summary>
/// Face grouping news for an album's host (its owner and the event's organisers: the only people who see the
/// People tab), never anyone else. No notification names a group or shows a face, and none says who opted out.
/// <list type="bullet">
/// <item>Grouping finished: once per switch-on, when the album's queue first runs dry with people found.</item>
/// <item>Opt-outs: "Someone opted out", at most once per album per <see cref="OptOutQuiet"/>.</item>
/// <item>Ending: a month and a week before grouping is switched off by itself, because the event's pass or
/// Premium ends (<see cref="EventPlan.CoveredUntil"/>) or the album has been idle for the retention limit
/// (<c>Faces:IdleAlbumDays</c>). The notice once it has happened comes from the switch itself
/// (<see cref="FacesSwitchedOff"/>).</item>
/// </list>
/// Registered in the API host only. On unless <c>Notifications:Faces:Enabled</c> is false.
/// </summary>
public sealed class FaceNoticeService(
    IServiceProvider services,
    IConfiguration config,
    ILogger<FaceNoticeService> logger) : BackgroundService
{
    public static readonly TimeSpan OptOutQuiet = TimeSpan.FromDays(1);
    public static readonly TimeSpan MonthBefore = TimeSpan.FromDays(30);
    public static readonly TimeSpan WeekBefore = TimeSpan.FromDays(7);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan EndingEvery = TimeSpan.FromHours(1);

    private bool Enabled => config.GetValue("Notifications:Faces:Enabled", true);

    private TimeSpan IdleLimit => TimeSpan.FromDays(int.TryParse(config["Faces:IdleAlbumDays"], out var d) && d > 0 ? d : 730);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var endingCheckedAt = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Enabled)
                {
                    using var scope = services.CreateScope();
                    var sp = scope.ServiceProvider;
                    var db = sp.GetRequiredService<AppDbContext>();
                    var people = sp.GetRequiredService<PushNotificationService>();
                    var sender = sp.GetRequiredService<IPushSender>();
                    var now = DateTimeOffset.UtcNow;
                    await ReadyAsync(db, people, sender, now, stoppingToken);
                    await OptOutsAsync(db, people, sender, now, stoppingToken);
                    if (now - endingCheckedAt >= EndingEvery)
                    {
                        endingCheckedAt = now;
                        await EndingAsync(db, people, sender, sp.GetService<IPlanService>(), IdleLimit, now, stoppingToken);
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Face notice sweep failed.");
            }
            await Task.Delay(SweepEvery, stoppingToken);
        }
    }

    private static Dictionary<string, string> Data(MediaBucket bucket, string path, string kind, string collapse) =>
        PushKinds.Data(path, kind, bucket.Id.ToString(), $"{collapse}-{bucket.Id:N}");

    // ----- Grouping finished --------------------------------------------------------------------------

    /// <summary>
    /// Albums whose grouping is on, not yet announced, with nothing waiting or running, and at least one group:
    /// the host is told once. An album with no faces found yet waits until there are some.
    /// </summary>
    public static async Task<int> ReadyAsync(
        AppDbContext db, PushNotificationService people, IPushSender sender, DateTimeOffset now, CancellationToken ct)
    {
        var done = await db.MediaBuckets
            .Where(b => b.FaceGroupingOn && b.FacesReadyPushedAt == null)
            .Where(b => !db.FaceJobs.Any(j => j.BucketId == b.Id && (j.Status == FaceJobStatus.Waiting || j.Status == FaceJobStatus.Running)))
            .Where(b => db.FacePeople.Any(p => p.BucketId == b.Id && !p.Ignored))
            .ToListAsync(ct);

        var sent = 0;
        foreach (var bucket in done)
        {
            // Claimed first, so a second sweep (or host) can never send it twice.
            var claimed = await db.MediaBuckets.Where(b => b.Id == bucket.Id && b.FacesReadyPushedAt == null && b.FaceGroupingOn)
                .ExecuteUpdateAsync(s => s.SetProperty(b => b.FacesReadyPushedAt, now), ct);
            if (claimed == 0) continue;

            var album = await people.AlbumTitleAsync(bucket, ct);
            foreach (var host in await people.AlbumHostsAsync(bucket, ct))
            {
                await sender.SendAsync(host, $"{album}: photos grouped by face",
                    "Face grouping has finished. Name the people in the People tab.",
                    Data(bucket, $"/buckets/{bucket.Id}?tab=people", PushKinds.Faces, "faces-ready"), ct);
                sent++;
            }
        }
        return sent;
    }

    // ----- Opt-outs -----------------------------------------------------------------------------------

    /// <summary>
    /// "Someone opted out of face grouping": counts only, never who, at most once per album per
    /// <see cref="OptOutQuiet"/>. A host who opted a group out themselves isn't told about that one.
    /// </summary>
    public static async Task<int> OptOutsAsync(
        AppDbContext db, PushNotificationService people, IPushSender sender, DateTimeOffset now, CancellationToken ct)
    {
        var quietSince = now - OptOutQuiet;
        var due = await db.MediaBuckets
            .Where(b => b.FacesOptOutPushedAt == null || b.FacesOptOutPushedAt <= quietSince)
            .Where(b => db.FacePeople.Any(p => p.BucketId == b.Id && p.Ignored && p.IgnoredAt != null && p.IgnoredAt <= now
                && (b.FacesOptOutPushedUpTo == null || p.IgnoredAt > b.FacesOptOutPushedUpTo)))
            .ToListAsync(ct);

        var sent = 0;
        foreach (var bucket in due)
        {
            var outs = await db.FacePeople
                .Where(p => p.BucketId == bucket.Id && p.Ignored && p.IgnoredAt != null && p.IgnoredAt <= now
                    && (bucket.FacesOptOutPushedUpTo == null || p.IgnoredAt > bucket.FacesOptOutPushedUpTo))
                .Select(p => new { At = p.IgnoredAt!.Value, p.IgnoredByUserId })
                .ToListAsync(ct);
            if (outs.Count == 0) continue;

            var album = await people.AlbumTitleAsync(bucket, ct);
            foreach (var host in await people.AlbumHostsAsync(bucket, ct))
            {
                var n = outs.Count(o => o.IgnoredByUserId != host);
                if (n == 0) continue;
                var body = n == 1
                    ? "Someone opted out of face grouping. Their face is no longer grouped or kept."
                    : $"{n} people opted out of face grouping. Their faces are no longer grouped or kept.";
                await sender.SendAsync(host, album, body,
                    Data(bucket, $"/buckets/{bucket.Id}?tab=people", PushKinds.Faces, "faces-optout"), ct);
                sent++;
            }

            var upTo = outs.Max(o => o.At);
            await db.MediaBuckets.Where(b => b.Id == bucket.Id).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.FacesOptOutPushedAt, now)
                .SetProperty(b => b.FacesOptOutPushedUpTo, upTo), ct);
        }
        return sent;
    }

    // ----- Ending -------------------------------------------------------------------------------------

    public enum EndReason { PlanEnds, Idle }

    /// <summary>
    /// When grouping will be switched off by itself, and why: the earlier of the plan's cover ending (unless it's
    /// a Premium that renews by itself) and the album reaching the idle limit. Null when neither is coming.
    /// </summary>
    public static async Task<(DateTimeOffset At, EndReason Reason)?> EndOfAsync(
        AppDbContext db, IPlanService? plans, MediaBucket bucket, TimeSpan idleLimit, CancellationToken ct)
    {
        var lastPhoto = await db.EventPhotos.Where(p => p.BucketId == bucket.Id).MaxAsync(p => (DateTimeOffset?)p.CreatedAt, ct);
        var active = lastPhoto is { } lp && lp > bucket.UpdatedAt ? lp : bucket.UpdatedAt;
        (DateTimeOffset At, EndReason Reason)? end = (active + idleLimit, EndReason.Idle);

        if (plans is not null && bucket.CampaignId != Guid.Empty)
        {
            EventPlan? plan = null;
            try { plan = await plans.ForCampaignAsync(bucket.CampaignId, ct); }
            catch (NotFoundException) { }
            if (plan is not null && PlanRules.IncludesFaceGrouping(plan)
                && await PlanEndsAsync(db, plan, bucket.CampaignId, ct) is { } until && until < end.Value.At)
                end = (until, EndReason.PlanEnds);
        }
        return end;
    }

    /// <summary>
    /// When the event stops having a plan that includes face grouping (PlanRules.IncludesFaceGrouping: anything
    /// but Free), or null when nothing ends it: a pass ends on its day unless the organiser's Premium carries on
    /// after it; Premium ends on its end date only when it won't renew by itself. (The photos' own cover,
    /// EventPlan.CoveredUntil, runs longer and isn't this.)
    /// </summary>
    public static async Task<DateTimeOffset?> PlanEndsAsync(AppDbContext db, EventPlan plan, Guid campaignId, CancellationToken ct)
    {
        var organiser = plan.OwnerUserId is { } owner
            ? await db.Users.Where(u => u.Id == owner)
                .Select(u => new { u.SubscriptionTier, u.SubscriptionEndsAt, u.AutoRenewKind }).FirstOrDefaultAsync(ct)
            : null;
        var now = DateTimeOffset.UtcNow;
        var premium = organiser is { SubscriptionTier: SubscriptionTier.Premium }
                      && PlanRules.IsActive(organiser.SubscriptionTier, organiser.SubscriptionEndsAt, now);
        // Null: Premium with no end, or one that renews by itself.
        DateTimeOffset? premiumEnd = premium && organiser!.SubscriptionEndsAt is { } e && organiser.AutoRenewKind is null ? e : null;
        var premiumForever = premium && premiumEnd is null;

        switch (plan.Kind)
        {
            case PlanKind.GoldPass or PlanKind.PlatinumPass:
                var passEnd = await db.Campaigns.Where(c => c.Id == campaignId).Select(c => c.EventPassUntil).FirstOrDefaultAsync(ct);
                if (passEnd is null || premiumForever) return null;
                return premium && premiumEnd > passEnd ? premiumEnd : passEnd;
            case PlanKind.Premium:
                return premiumEnd;
            default:
                return null;
        }
    }

    /// <summary>The month and week warnings. A changed end day (a pass extended, a photo added) starts them over.</summary>
    public static async Task<int> EndingAsync(
        AppDbContext db, PushNotificationService people, IPushSender sender, IPlanService? plans,
        TimeSpan idleLimit, DateTimeOffset now, CancellationToken ct)
    {
        var on = await db.MediaBuckets.Where(b => b.FaceGroupingOn).ToListAsync(ct);
        var sent = 0;
        foreach (var bucket in on)
        {
            if (await EndOfAsync(db, plans, bucket, idleLimit, ct) is not { } end || end.At <= now) continue;
            var left = end.At - now;
            var stage = left <= WeekBefore ? 2 : left <= MonthBefore ? 1 : 0;

            // Within an hour counts as the same day being warned about (idle ends move with every sweep's clock).
            var sameEnd = bucket.FacesEndNoticeFor is { } warned && (warned - end.At).Duration() < TimeSpan.FromDays(1);
            var already = sameEnd ? bucket.FacesEndNoticeStage : 0;
            if (stage <= already)
            {
                if (!sameEnd)
                    await db.MediaBuckets.Where(b => b.Id == bucket.Id).ExecuteUpdateAsync(s => s
                        .SetProperty(b => b.FacesEndNoticeFor, end.At).SetProperty(b => b.FacesEndNoticeStage, 0), ct);
                continue;
            }

            await db.MediaBuckets.Where(b => b.Id == bucket.Id).ExecuteUpdateAsync(s => s
                .SetProperty(b => b.FacesEndNoticeFor, sameEnd ? bucket.FacesEndNoticeFor : end.At)
                .SetProperty(b => b.FacesEndNoticeStage, stage), ct);

            var zone = bucket.CampaignId != Guid.Empty
                ? await db.Campaigns.Where(c => c.Id == bucket.CampaignId).Select(c => c.TimeZone).FirstOrDefaultAsync(ct)
                : null;
            var day = EventTime.Local(end.At, zone).ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("en-GB"));
            var album = await people.AlbumTitleAsync(bucket, ct);
            var body = end.Reason == EndReason.PlanEnds
                ? "When the event's pass or Premium ends, face grouping is switched off and its face data deleted."
                : "The album has had no new photos for a long time, so face grouping will be switched off and its face data deleted. A new photo keeps it on.";
            foreach (var host in await people.AlbumHostsAsync(bucket, ct))
            {
                await sender.SendAsync(host, $"Face grouping in {album} ends on {day}", body,
                    Data(bucket, $"/buckets/{bucket.Id}", PushKinds.Album, "faces-end"), ct);
                sent++;
            }
        }
        return sent;
    }
}
