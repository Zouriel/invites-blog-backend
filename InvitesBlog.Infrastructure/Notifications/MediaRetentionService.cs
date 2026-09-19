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
/// What happens to an event's photos after its plan runs out.
///
/// <para>Day 0: uploads stop and the organiser is emailed. Day 23: a reminder. Day 30: only the
/// organiser can look (enforced where photos are read, from <see cref="MediaPhase"/>). Day 83: a
/// final notice. Day 90: the photos are removed. Renewing at any point moves the cover end, which
/// starts the emails over.</para>
///
/// <para>Runs inside the API host: there is no separate worker in production.</para>
/// </summary>
public sealed class MediaRetentionService(
    IServiceProvider services,
    IConfiguration config,
    ILogger<MediaRetentionService> logger) : BackgroundService
{
    private static readonly TimeSpan SweepEvery = TimeSpan.FromHours(6);

    private bool Enabled => config.GetValue("Notifications:MediaRetention:Enabled", true);

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

    /// <summary>"Keep your photos" as the price book says at the start of each sweep, for the notices.</summary>
    private decimal _keepPrice = PlanCatalog.KeepPhotosYearly;

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var plans = scope.ServiceProvider.GetRequiredService<IPlanService>();
        _keepPrice = (await scope.ServiceProvider.GetRequiredService<IPriceBook>().CurrentAsync(ct)).KeepPhotosYearly;
        var email = scope.ServiceProvider.GetRequiredService<IEmailSender>();
        var now = DateTimeOffset.UtcNow;

        // Only events that still hold something.
        var withBuckets = await db.MediaBuckets.Where(b => b.UsedBytes > 0).Select(b => b.CampaignId).Distinct().ToListAsync(ct);
        var withPhotos = await db.EventPhotos.Where(p => p.DeletedAt == null && p.CampaignId != null)
            .Select(p => p.CampaignId!.Value).Distinct().ToListAsync(ct);
        // Events cancelled before their photos were deleted on cancel still have buckets to clear.
        var cancelled = await db.MediaBuckets
            .Where(b => db.Campaigns.Any(c => c.Id == b.CampaignId && c.Status == CampaignStatus.Cancelled))
            .Select(b => b.CampaignId).Distinct().ToListAsync(ct);
        var ids = withBuckets.Concat(withPhotos).Concat(cancelled).Distinct().ToList();

        foreach (var id in ids)
        {
            try
            {
                await SweepEventAsync(db, plans, email, id, now, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One bad event must not stop the sweep for every other one.
                logger.LogError(ex, "Media retention failed for event {CampaignId}.", id);
            }
        }
    }

    private async Task SweepEventAsync(
        AppDbContext db, IPlanService plans, IEmailSender email, Guid campaignId, DateTimeOffset now, CancellationToken ct)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null) return;

        if (campaign.Status == CampaignStatus.Cancelled)
        {
            await RemoveMediaAsync(db, campaignId, now, ct);
            db.MediaBucketQrs.RemoveRange(db.MediaBucketQrs.Where(q => db.MediaBuckets.Any(b => b.Id == q.BucketId && b.CampaignId == campaignId)));
            db.MediaBuckets.RemoveRange(db.MediaBuckets.Where(b => b.CampaignId == campaignId));
            campaign.MediaDeletedAt ??= now;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Removed the photos of cancelled event {CampaignId}.", campaignId);
            return;
        }
        if (campaign.MediaDeletedAt is not null) return;

        var plan = await plans.ForCampaignAsync(campaignId, ct);
        if (plan.CoveredUntil is not { } end || now < end)
        {
            if (campaign.MediaNoticeStage != 0)
            {
                campaign.MediaNoticeStage = 0;
                campaign.MediaNoticeAnchor = null;
                await db.SaveChangesAsync(ct);
            }
            return;
        }

        if (campaign.MediaNoticeAnchor != end)
        {
            campaign.MediaNoticeAnchor = end;
            campaign.MediaNoticeStage = 0;
        }

        var days = (now - end).TotalDays;
        if (days >= PlanCatalog.DeleteDay)
        {
            await RemoveMediaAsync(db, campaignId, now, ct);
            campaign.MediaDeletedAt = now;
            campaign.MediaNoticeStage = 4;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Removed the photos of event {CampaignId}; its plan ended {End}.", campaignId, end);
            return;
        }

        // Only the latest notice that is due, so a sweep that runs late doesn't send three at once.
        var due = days >= PlanCatalog.FinalNoticeDay ? 3 : days >= PlanCatalog.ReminderDay ? 2 : 1;
        if (campaign.MediaNoticeStage >= due)
        {
            await db.SaveChangesAsync(ct);
            return;
        }

        var recipients = await RecipientsAsync(db, campaign, plan, ct);
        var (subject, body) = Notice(due, campaign, end);
        foreach (var to in recipients)
            await email.SendAsync(new EmailMessage(
                To: to, Subject: subject, Html: body, Stream: EmailStream.System,
                Tags: new[] { new KeyValuePair<string, string>("kind", $"media_retention_{due}") }), ct);

        campaign.MediaNoticeStage = due;
        await db.SaveChangesAsync(ct);
    }

    private static async Task RemoveMediaAsync(AppDbContext db, Guid campaignId, DateTimeOffset now, CancellationToken ct)
    {
        var bucketIds = await db.MediaBuckets.Where(b => b.CampaignId == campaignId).Select(b => b.Id).ToListAsync(ct);
        var photos = await db.EventPhotos
            .Where(p => p.DeletedAt == null
                        && (p.CampaignId == campaignId || (p.BucketId != null && bucketIds.Contains(p.BucketId.Value))))
            .ToListAsync(ct);
        foreach (var photo in photos) photo.DeletedAt = now;

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

    private (string Subject, string Html) Notice(int stage, Campaign campaign, DateTimeOffset end)
    {
        var baseUrl = config.InviterBase();
        var dashboard = $"{baseUrl}/dashboard/{campaign.Id}";
        var pricing = $"{baseUrl}/pricing";
        var title = WebUtility.HtmlEncode(campaign.Title);
        var organiserOnly = end.AddDays(PlanCatalog.OrganiserOnlyDay).ToString("d MMMM yyyy");
        var deleteOn = end.AddDays(PlanCatalog.DeleteDay).ToString("d MMMM yyyy");

        var (subject, lines) = stage switch
        {
            1 => ($"The photos from {campaign.Title}: guests can no longer add to them",
                $"Guests can no longer add photos to <strong>{title}</strong>. " +
                $"Guests can still look at the photos until {organiserOnly}. After that only you can, " +
                $"and the photos are removed on {deleteOn}. Download everything now, or keep them online for another year " +
                $"({PlanCatalog.Currency} {_keepPrice:0} a year)."),
            2 => ($"One week left for guests to see the photos from {campaign.Title}",
                $"From {organiserOnly}, only you will be able to see the photos from <strong>{title}</strong>. " +
                $"They are removed on {deleteOn} unless you keep them online for another year."),
            _ => ($"The photos from {campaign.Title} will be removed in 7 days",
                $"The photos and videos from <strong>{title}</strong> will be removed on {deleteOn}. " +
                "Download everything now, or keep them online for another year. Your invitation, guest list and replies stay."),
        };

        var html =
            "<div style=\"font-family:-apple-system,'Segoe UI',Roboto,Arial,sans-serif;max-width:520px;margin:0 auto;padding:24px;color:#152026\">" +
            $"<p style=\"font-size:16px;line-height:1.6\">{lines}</p>" +
            $"<p style=\"text-align:center;margin:28px 0 12px\"><a href=\"{dashboard}\" style=\"display:inline-block;background:#1b3d59;color:#fff;text-decoration:none;padding:14px 30px;border-radius:999px;font-weight:600\">Open the event and download</a></p>" +
            $"<p style=\"text-align:center;margin:0 0 28px\"><a href=\"{pricing}\" style=\"color:#1b3d59\">Keep your photos</a></p>" +
            "<p style=\"font-size:12px;color:#4a6378;line-height:1.6\">Sent via invites.blog</p></div>";

        return (subject, html);
    }
}
