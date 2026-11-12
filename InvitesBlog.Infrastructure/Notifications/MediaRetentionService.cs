using InvitesBlog.Domain.Enums;
using System.Net;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using InvitesBlog.Application.Common;

namespace InvitesBlog.Infrastructure.Notifications;

/// <summary>
/// What happens to an event's photos after its pass (or its organiser's Premium) runs out.
///
/// <para>Owner, 2026-11-03: free albums are kept forever, and "passes if not paid will be reduced to
/// unsubed size and everything above it gets removed", "with appropriate warnings in times for them
/// to backup". So a free event (never on a pass or Premium) is never touched. When a cover ends the
/// event goes back to Free: uploads stop, everyone can still look, and if it holds more than Free's
/// space the organiser is warned by email and push: at once (the 30-day notice, which sets the date
/// of the cut at least <see cref="PlanCatalog.TrimNoticeDays"/> days away), a week before and a day
/// before. On the day, what doesn't fit in Free's space is removed (<see cref="MediaTrim"/>) and the
/// event is a free event from then on, kept for good. Nothing is removed unless the 30-day notice and
/// at least one more have gone out. Extending, "Keep your photos" or Premium again moves the cover
/// end, which calls the cut off.</para>
///
/// <para>A cancelled event's photos go at once; so do the albums of an event that was deleted.</para>
///
/// <para>Runs inside the API host: there is no separate worker in production. With
/// <c>Notifications:MediaRetention:DryRun</c> it only logs what it would do, writing and sending
/// nothing.</para>
/// </summary>
public sealed class MediaRetentionService(
    IServiceProvider services,
    IConfiguration config,
    ILogger<MediaRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromHours(6);

    private bool Enabled => config.GetValue("Notifications:MediaRetention:Enabled", true);
    private bool DryRun => config.GetValue("Notifications:MediaRetention:DryRun", false);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Let the app finish starting (and migrating) before the first sweep.
        await Task.Delay(TimeSpan.FromMinutes(2), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Enabled) await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Media retention sweep failed.");
            }
            await Task.Delay(SweepEvery, stoppingToken);
        }
    }

    /// <summary>What one sweep did (or, dry, would have done).</summary>
    public sealed record SweepReport(int Events, int Warned, int Trimmed, int FilesRemoved, long BytesRemoved);

    private int _warned, _trimmed, _files;
    private long _bytes;

    public Task<SweepReport> RunOnceAsync(CancellationToken ct) => RunOnceAsync(DateTimeOffset.UtcNow, ct);

    public async Task<SweepReport> RunOnceAsync(DateTimeOffset now, CancellationToken ct)
    {
        _warned = _trimmed = _files = 0;
        _bytes = 0;
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var plans = scope.ServiceProvider.GetRequiredService<IPlanService>();
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var push = scope.ServiceProvider.GetService<IPushSender>();
        var offers = scope.ServiceProvider.GetService<IPassOfferService>();
        _storage = scope.ServiceProvider.GetRequiredService<IStorageService>();

        // Only events that still hold something.
        var withBuckets = await db.MediaBuckets.Where(b => b.UsedBytes > 0).Select(b => b.CampaignId).Distinct().ToListAsync(ct);
        var withPhotos = await db.EventPhotos.Where(p => p.DeletedAt == null && p.CampaignId != null)
            .Select(p => p.CampaignId!.Value).Distinct().ToListAsync(ct);
        // Events cancelled before their photos were deleted on cancel still have buckets to clear.
        var cancelled = await db.MediaBuckets
            .Where(b => db.Campaigns.Any(c => c.Id == b.CampaignId && c.Status == CampaignStatus.Cancelled))
            .Select(b => b.CampaignId).Distinct().ToListAsync(ct);
        // Albums whose event was deleted. Deleting an event removes its guests and invitations but
        // used to leave its albums and photos behind, still stored and still counted.
        var orphaned = await db.MediaBuckets
            .Where(b => !db.Campaigns.Any(c => c.Id == b.CampaignId))
            .Select(b => b.CampaignId).Distinct().ToListAsync(ct);
        var ids = withBuckets.Concat(withPhotos).Concat(cancelled).Concat(orphaned).Distinct().ToList();

        foreach (var id in ids)
        {
            try
            {
                await SweepEventAsync(db, plans, email, push, offers, id, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad event must not stop the sweep for every other one.
                logger.LogError(ex, "Media retention failed for event {CampaignId}.", id);
            }
        }

        // Face data's own limit (the Privacy Policy's written retention policy): albums idle for 24 months lose it.
        if (!DryRun && scope.ServiceProvider.GetService<Application.Services.Faces.IFaceGroupingService>() is { } faces)
        {
            try
            {
                var idleDays = int.TryParse(config["Faces:IdleAlbumDays"], out var d) && d > 0 ? d : 730;
                var expired = await faces.ExpireIdleAsync(TimeSpan.FromDays(idleDays), ct);
                if (expired > 0) logger.LogInformation("Face grouping switched off in {Count} idle albums (face data deleted).", expired);
                var unpaid = await faces.ExpireUnpaidAsync(ct);
                if (unpaid > 0) logger.LogInformation("Face grouping switched off in {Count} albums without a plan that includes it (face data deleted).", unpaid);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Expiring idle face data failed.");
            }
        }

        var report = new SweepReport(ids.Count, _warned, _trimmed, _files, _bytes);
        logger.LogInformation(
            "Media retention sweep{Dry}: {Events} events with photos, {Warned} warned, {Trimmed} cut down to Free, {Files} files and {Bytes} bytes removed.",
            DryRun ? " (dry run, nothing changed)" : "", report.Events, report.Warned, report.Trimmed, report.FilesRemoved, report.BytesRemoved);
        return report;
    }

    private async Task SweepEventAsync(
        AppDbContext db, IPlanService plans, IEmailSender email, IPushSender? push, IPassOfferService? offers,
        Guid campaignId, DateTimeOffset now, CancellationToken ct)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null)
        {
            // The event is gone, so nothing may show these photos again: they go, with their albums.
            if (DryRun) { logger.LogInformation("Dry run: would remove the albums of deleted event {CampaignId}.", campaignId); return; }
            await RemoveMediaAsync(db, campaignId, now, ct);
            await RemoveBucketsAsync(db, campaignId, ct);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Removed the albums of deleted event {CampaignId}.", campaignId);
            return;
        }

        if (campaign.Status == CampaignStatus.Cancelled)
        {
            if (DryRun) { logger.LogInformation("Dry run: would remove the photos of cancelled event {CampaignId}.", campaignId); return; }
            await RemoveMediaAsync(db, campaignId, now, ct);
            await RemoveBucketsAsync(db, campaignId, ct);
            campaign.MediaDeletedAt ??= now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Removed the photos of cancelled event {CampaignId}.", campaignId);
            return;
        }
        if (campaign.MediaDeletedAt is not null) return;

        var plan = await plans.ForCampaignAsync(campaignId, ct);
        if (plan.CoveredUntil is not { } end || now < end)
        {
            // Covered, or a free event kept for good: nothing pending.
            if (campaign.MediaNoticeStage is not (0 or 4) || campaign.MediaTrimAt is not null || campaign.MediaTrimBytes is not null)
            {
                campaign.MediaNoticeStage = campaign.MediaTrimmedAt is not null && plan.CoveredUntil is null ? 4 : 0;
                campaign.MediaNoticeAnchor = null;
                campaign.MediaTrimAt = null;
                campaign.MediaTrimBytes = null;
                if (!DryRun) await db.SaveChangesAsync(ct);
            }
            return;
        }

        if (campaign.MediaNoticeAnchor != end)
        {
            // A new end (a first lapse, or one after a renewal ran out): its warnings start over.
            campaign.MediaNoticeAnchor = end;
            campaign.MediaNoticeStage = 0;
            campaign.MediaTrimAt = null;
        }

        var cut = await PlanCutAsync(db, campaign, ct);
        campaign.MediaTrimBytes = cut.RemoveBytes;

        if (cut.Remove.Count == 0)
        {
            // Everything fits in Free's space: it simply becomes a free event, kept for good.
            campaign.MediaTrimmedAt = now;
            campaign.MediaNoticeStage = 4;
            campaign.MediaTrimAt = null;
            campaign.MediaTrimBytes = 0;
            if (!DryRun) await db.SaveChangesAsync(ct);
            logger.LogInformation("Event {CampaignId} went back to Free with nothing to remove{Dry}.", campaignId, DryRun ? " (dry run)" : "");
            return;
        }

        var trimAt = campaign.MediaTrimAt;
        if (campaign.MediaNoticeStage >= 2 && trimAt is { } due && now >= due)
        {
            await TrimAsync(db, campaign, cut, now, ct);
            return;
        }

        // The warning that is due now. Only the latest, so a late sweep doesn't send several at once.
        int stage;
        if (campaign.MediaNoticeStage == 0 || trimAt is null)
        {
            stage = 1;
            // At least the full notice from today, whenever the cover actually ended: an event already
            // past its end when this shipped gets its 30 days from now.
            trimAt = now.AddDays(PlanCatalog.TrimNoticeDays);
        }
        else
        {
            var daysLeft = (trimAt.Value - now).TotalDays;
            stage = daysLeft <= PlanCatalog.TrimFinalDaysBefore ? 3
                : daysLeft <= PlanCatalog.TrimReminderDaysBefore ? 2
                : 1;
            // The second warning is what allows the cut. If it is going out late, it still promises a
            // real date: never sooner than a day away.
            if (stage >= 2 && campaign.MediaNoticeStage < 2)
            {
                stage = 2;
                var earliest = now.AddDays(PlanCatalog.TrimFinalDaysBefore);
                if (trimAt < earliest) trimAt = earliest;
            }
        }

        if (campaign.MediaNoticeStage >= stage)
        {
            if (!DryRun) await db.SaveChangesAsync(ct);
            return;
        }

        var recipients = await RecipientsAsync(db, campaign, plan, ct);
        if (recipients.Count == 0 && plan.OwnerUserId is null)
        {
            // Nobody to warn means nothing may be removed: the event stays as it is.
            logger.LogWarning("Event {CampaignId} went back to Free but has nobody to warn; nothing will be removed.", campaignId);
            if (!DryRun) await db.SaveChangesAsync(ct);
            return;
        }

        campaign.MediaTrimAt = trimAt;
        _warned++;
        if (DryRun)
        {
            logger.LogInformation("Dry run: would send warning {Stage} for event {CampaignId}: {Files} files, {Bytes} bytes above Free, removed on {TrimAt:yyyy-MM-dd}.",
                stage, campaignId, cut.Remove.Count, cut.RemoveBytes, trimAt);
            return;
        }

        var extension = campaign.EventPass != EventPassKind.None && offers is not null
            ? (await offers.ForCampaignAsync(campaign.Id, ct)).ExtensionPrice(campaign.EventPass)
            : (decimal?)null;
        var (subject, body) = Notice(stage, campaign, trimAt.Value, cut.RemoveBytes, extension);
        foreach (var to in recipients)
            await email.SendAsync(new EmailMessage(
                To: to, Subject: subject, Html: body, Stream: EmailStream.System,
                Tags: new[] { new KeyValuePair<string, string>("kind", $"media_trim_{stage}") }), ct);

        campaign.MediaNoticeStage = stage;
        // Saved before the push: a push sender may save the unit of work itself.
        await db.SaveChangesAsync(ct);

        if (push is not null && plan.OwnerUserId is { } owner)
            await push.SendAsync(owner, PushTitle(stage, campaign, trimAt.Value), PushBody(InvitesBlog.Application.Events.EventTime.Local(trimAt.Value, campaign.TimeZone), cut.RemoveBytes),
                // Each warning replaces the one before it on the phone.
                Application.Push.PushKinds.Data($"/dashboard/{campaign.Id}", Application.Push.PushKinds.Album,
                    campaign.Id.ToString(), $"trim-{campaign.Id:N}"), ct);
    }

    /// <summary>The event's live photos and videos, and what of them doesn't fit in Free's space.</summary>
    private static async Task<MediaTrim.Plan> PlanCutAsync(AppDbContext db, Campaign campaign, CancellationToken ct)
    {
        var bucketIds = await db.MediaBuckets.Where(b => b.CampaignId == campaign.Id).Select(b => b.Id).ToListAsync(ct);
        var items = await db.EventPhotos
            .Where(p => p.DeletedAt == null
                        && (p.CampaignId == campaign.Id || (p.BucketId != null && bucketIds.Contains(p.BucketId.Value))))
            .Select(p => new MediaTrim.Item(p.Id, p.SizeBytes, p.CreatedAt, p.KeepWhenTrimmed))
            .ToListAsync(ct);
        return MediaTrim.For(items, campaign.PostCoverPhotoIds, PlanCatalog.FreeEventBytes);
    }

    /// <summary>Removes what doesn't fit, frees its space and files, and makes it a free event.</summary>
    private async Task TrimAsync(AppDbContext db, Campaign campaign, MediaTrim.Plan cut, DateTimeOffset now, CancellationToken ct)
    {
        _trimmed++;
        _files += cut.Remove.Count;
        _bytes += cut.RemoveBytes;
        if (DryRun)
        {
            logger.LogInformation("Dry run: would cut event {CampaignId} down to Free: {Files} files, {Bytes} bytes.",
                campaign.Id, cut.Remove.Count, cut.RemoveBytes);
            return;
        }

        var ids = cut.Remove.ToList();
        var photos = await db.EventPhotos.Where(p => ids.Contains(p.Id) && p.DeletedAt == null).ToListAsync(ct);
        foreach (var photo in photos) photo.DeletedAt = now;
        campaign.MediaTrimmedAt = now;
        campaign.MediaNoticeStage = 4;
        campaign.MediaTrimAt = null;
        campaign.MediaTrimBytes = 0;
        await db.SaveChangesAsync(ct);

        // Their faces (biometric data) go with them.
        await db.PhotoFaces.Where(f => ids.Contains(f.PhotoId)).ExecuteDeleteAsync(ct);
        await db.FaceJobs.Where(j => ids.Contains(j.Id)).ExecuteDeleteAsync(ct);

        // Space back, in the database directly (UsedBytes is ignored on a tracked save; see AppDbContext).
        foreach (var group in photos.Where(p => p.BucketId != null).GroupBy(p => p.BucketId!.Value))
        {
            var freed = group.Sum(p => p.SizeBytes);
            await db.MediaBuckets.Where(b => b.Id == group.Key)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(b => b.UsedBytes, b => b.UsedBytes - freed < 0 ? 0 : b.UsedBytes - freed)
                    .SetProperty(b => b.UpdatedAt, now), ct);
        }

        if (_storage is { } storage)
            foreach (var key in photos.SelectMany(p => new[] { p.OriginalUrl, p.Url, p.ThumbUrl })
                         .Select(storage.KeyFor).OfType<string>().Distinct())
            {
                try
                {
                    await storage.DeleteAsync(key, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Couldn't remove {Key} while cutting event {CampaignId} down to Free.", key, campaign.Id);
                }
            }

        logger.LogInformation("Cut event {CampaignId} down to Free: removed {Files} files, {Bytes} bytes.",
            campaign.Id, photos.Count, photos.Sum(p => p.SizeBytes));
    }

    private IStorageService? _storage;

    private static Task RemoveBucketsAsync(AppDbContext db, Guid campaignId, CancellationToken ct)
    {
        db.MediaBucketQrs.RemoveRange(db.MediaBucketQrs.Where(q => db.MediaBuckets.Any(b => b.Id == q.BucketId && b.CampaignId == campaignId)));
        db.MediaBuckets.RemoveRange(db.MediaBuckets.Where(b => b.CampaignId == campaignId));
        return Task.CompletedTask;
    }

    /// <summary>
    /// Deletes all of a cancelled or deleted event's photos: marks every row deleted and removes the
    /// files behind them, ones deleted earlier included.
    /// </summary>
    private async Task RemoveMediaAsync(AppDbContext db, Guid campaignId, DateTimeOffset now, CancellationToken ct)
    {
        var bucketIds = await db.MediaBuckets.Where(b => b.CampaignId == campaignId).Select(b => b.Id).ToListAsync(ct);
        var photos = await db.EventPhotos
            .Where(p => p.CampaignId == campaignId || (p.BucketId != null && bucketIds.Contains(p.BucketId.Value)))
            .ToListAsync(ct);
        foreach (var photo in photos) photo.DeletedAt ??= now;

        // The event is gone: so is all its face data (faces, groups, queued photos).
        await db.PhotoFaces.Where(f => bucketIds.Contains(f.BucketId)).ExecuteDeleteAsync(ct);
        await db.FacePeople.Where(f => bucketIds.Contains(f.BucketId)).ExecuteDeleteAsync(ct);
        await db.FaceJobs.Where(f => bucketIds.Contains(f.BucketId)).ExecuteDeleteAsync(ct);
        await db.MediaBuckets.Where(b => bucketIds.Contains(b.Id) && b.FaceGroupingOn)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.FaceGroupingOn, false), ct);

        if (_storage is { } storage)
        {
            foreach (var key in photos.SelectMany(p => new[] { p.OriginalUrl, p.Url, p.ThumbUrl })
                         .Select(storage.KeyFor).OfType<string>().Distinct())
            {
                try
                {
                    await storage.DeleteAsync(key, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogWarning(ex, "Couldn't remove {Key} while removing event {CampaignId}'s photos.", key, campaignId);
                }
            }
        }

        // In the database directly: UsedBytes is ignored on a tracked save (see AppDbContext), so that
        // no stale copy of it can overwrite an upload counted meanwhile. This commits ahead of the
        // caller's SaveChanges; if that save then fails, the photos are still live and the next sweep
        // finds this event again and runs the whole removal once more.
        await db.MediaBuckets
            .Where(b => b.CampaignId == campaignId)
            .ExecuteUpdateAsync(s => s.SetProperty(b => b.UsedBytes, 0L).SetProperty(b => b.UpdatedAt, now), ct);
    }

    /// <summary>The organiser, and anyone the event is for who has full access.</summary>
    private static async Task<List<string>> RecipientsAsync(
        AppDbContext db, Campaign campaign, EventPlan plan, CancellationToken ct)
    {
        var list = new List<string>();
        if (plan.OwnerUserId is { } owner
            && await db.Users.Where(u => u.Id == owner).Select(u => u.Email).FirstOrDefaultAsync(ct) is { } ownerEmail)
            list.Add(ownerEmail);
        else if (campaign.InviterId is { } inviterId
                 && await db.Inviters.Where(i => i.Id == inviterId).Select(i => i.Email).FirstOrDefaultAsync(ct) is { } inviterEmail)
            list.Add(inviterEmail);

        list.AddRange(await db.CampaignCelebrants
            .Where(c => c.CampaignId == campaign.Id && c.CanManage && c.Email != null)
            .Select(c => c.Email!)
            .ToListAsync(ct));

        return list.Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e.Trim().ToLowerInvariant()).Distinct().ToList();
    }

    /// <summary>"1.4 GB", "350 MB": how much will be removed, in the words the pages use.</summary>
    public static string Size(long bytes) =>
        bytes >= PlanCatalog.Gb
            ? $"{bytes / (double)PlanCatalog.Gb:0.#} GB"
            : $"{Math.Max(1, (long)Math.Ceiling(bytes / (double)PlanCatalog.Mb))} MB";

    private static string FreeSize => Size(PlanCatalog.FreeEventBytes);

    private static string PushTitle(int stage, Campaign campaign, DateTimeOffset trimAt) => stage switch
    {
        1 => $"{campaign.Title} is back on Free",
        2 => $"A week left to keep everything from {campaign.Title}",
        _ => $"Tomorrow: photos above {FreeSize} from {campaign.Title} are removed",
    };

    /// <summary>
    /// The day is the event's own (the caller passes it in the event's zone). No selling: the app shows this as
    /// it is, and the app may not point people at buying (the email and the dashboard carry the options).
    /// </summary>
    public static string PushBody(DateTimeOffset trimAt, long bytes) =>
        $"On {trimAt.ToString("d MMMM", System.Globalization.CultureInfo.GetCultureInfo("en-GB"))} about {Size(bytes)} above Free's {FreeSize} will be removed. Download what you want to keep before then.";

    private (string Subject, string Html) Notice(int stage, Campaign campaign, DateTimeOffset trimAt, long bytes, decimal? extension)
    {
        var baseUrl = config.InviterBase();
        var dashboard = $"{baseUrl}/dashboard/{campaign.Id}";
        var title = WebUtility.HtmlEncode(campaign.Title);
        // The day on the event's own clock (the server runs on UTC).
        var on = InvitesBlog.Application.Events.EventTime.Local(trimAt, campaign.TimeZone)
            .ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-GB"));
        var size = Size(bytes);
        var pass = campaign.EventPass == EventPassKind.Platinum ? "Platinum pass" : campaign.EventPass == EventPassKind.Gold ? "Gold pass" : null;
        var keep = pass is not null && extension is { } price
            ? $"extend the {pass} for a year ({PlanCatalog.Currency} {price:0})"
            : "subscribe to Premium again";

        var (subject, lead) = stage switch
        {
            1 => ($"{campaign.Title} goes back to Free on {on}",
                $"{(pass is null ? "Your Premium" : $"The {pass} for <strong>{title}</strong>")} has ended, so the event goes back to Free. " +
                $"Free events are kept for good, up to {FreeSize}."),
            2 => ($"One week left to keep everything from {campaign.Title}",
                $"<strong>{title}</strong> goes back to Free on {on}."),
            _ => ($"Tomorrow: photos above {FreeSize} from {campaign.Title} are removed",
                $"<strong>{title}</strong> goes back to Free on {on}."),
        };
        var lines =
            $"{lead} On {on}, anything above {FreeSize} will be removed: about {size} of photos and videos. " +
            $"To keep everything, {keep}, or download everything before then. " +
            "The event's cover photos are kept first, then the earliest uploads. " +
            "Your invitation, guest list and replies always stay.";

        var html =
            "<div style=\"font-family:-apple-system,'Segoe UI',Roboto,Arial,sans-serif;max-width:520px;margin:0 auto;padding:24px;color:#152026\">" +
            $"<p style=\"font-size:16px;line-height:1.6\">{lines}</p>" +
            $"<p style=\"text-align:center;margin:28px 0 12px\"><a href=\"{dashboard}\" style=\"display:inline-block;background:#7A0C1E;color:#fff;text-decoration:none;padding:14px 30px;border-radius:999px;font-weight:600\">Download everything</a></p>" +
            $"<p style=\"text-align:center;margin:0 0 28px\"><a href=\"{dashboard}\" style=\"color:#7A0C1E\">{(pass is null ? "Get Premium again" : "Extend the pass")}</a></p>" +
            "<p style=\"font-size:12px;color:#4a6378;line-height:1.6\">Sent via invites.blog</p></div>";

        return (subject, html);
    }
}
