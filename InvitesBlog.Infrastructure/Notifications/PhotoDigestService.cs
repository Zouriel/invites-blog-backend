using InvitesBlog.Application.Abstractions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Email;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Notifications;

/// <summary>
/// Tells an event's people that new photos have arrived (§5) — once per campaign per quiet period,
/// never once per photo.
///
/// <para><b>Why a digest and not an event.</b> The obvious build is "email on upload", and it is
/// unusable: a wedding with eighty guests shooting freely is thousands of photos. Batching per upload
/// ACTION is better and still wrong — fifteen guests uploading three times each over one evening is
/// fifty emails to everyone on the list, including notifications about photos they took themselves.
/// The unit that works is TIME. Everything uploaded since the last notice collapses into one message,
/// and nothing goes out until <c>WindowHours</c> has passed since that notice.</para>
///
/// <para><b>Where it runs.</b> Registered by the API host, not the worker: the worker project is not
/// deployed, so a sweep living there would never have run. It must be registered in exactly ONE host —
/// two sweeps racing the same campaign would both read the marker before either wrote it, and everyone
/// would get the digest twice.</para>
///
/// <para><b>Off unless switched on.</b> This mails real guests, so it does nothing at all unless
/// <c>Notifications:PhotoDigest:Enabled</c> is true. Shipping it dark means it can be reviewed
/// against a live campaign before anyone's phone buzzes.</para>
/// </summary>
public sealed class PhotoDigestService(
    IServiceProvider services,
    IConfiguration config,
    ILogger<PhotoDigestService> logger) : BackgroundService
{
    /// <summary>How often to look. The quiet period does the real limiting; this only sets latency.</summary>
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(15);

    private bool Enabled => config.GetValue("Notifications:PhotoDigest:Enabled", false);

    private TimeSpan Window =>
        TimeSpan.FromHours(config.GetValue("Notifications:PhotoDigest:WindowHours", 6));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Enabled) await RunOnceAsync(stoppingToken);
            }
            catch (Exception ex)
            {
                // One bad campaign must not stop the sweep for every other one.
                logger.LogError(ex, "Photo digest sweep failed.");
            }
            await Task.Delay(SweepEvery, stoppingToken);
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        await SweepAsync(
            scope.ServiceProvider.GetRequiredService<AppDbContext>(),
            scope.ServiceProvider.GetRequiredService<IEmailSender>(),
            ct);
    }

    /// <summary>
    /// The sweep itself, over a database and a sender handed to it. Split from <see cref="RunOnceAsync"/>
    /// so the rules that decide who hears about what can be tested without standing up a container.
    /// </summary>
    public async Task SweepAsync(AppDbContext db, IEmailSender email, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        // Hoisted so the comparison is against a plain value; `now - Window` inside the predicate
        // reads as something for the provider to translate.
        var quietUntil = now - Window;

        // Campaigns whose quiet period has elapsed. A campaign that has never sent one is eligible
        // immediately — there is no previous notice to be quiet after.
        var due = await db.Campaigns
            .Where(c => c.Status != CampaignStatus.Cancelled
                        && (c.PhotosNotifiedAt == null || c.PhotosNotifiedAt < quietUntil))
            .ToListAsync(ct);

        foreach (var campaign in due)
        {
            var since = campaign.PhotosNotifiedAt ?? DateTimeOffset.MinValue;
            var fresh = await db.EventPhotos
                .Where(p => p.CampaignId == campaign.Id && p.DeletedAt == null && p.CreatedAt > since)
                .ToListAsync(ct);

            if (fresh.Count == 0) continue;

            var sent = await NotifyAsync(db, email, campaign, fresh, ct);

            // Stamped whether or not anyone was reachable: the point of the marker is "these photos
            // have been accounted for". Leaving it unset on a campaign with no addressable guests
            // would re-examine the same photos every sweep, forever.
            campaign.PhotosNotifiedAt = now;
            await db.SaveChangesAsync(ct);

            logger.LogInformation(
                "Photo digest for campaign {CampaignId}: {Photos} photos, {Sent} recipients.",
                campaign.Id, fresh.Count, sent);
        }
    }

    private async Task<int> NotifyAsync(
        AppDbContext db, IEmailSender email, Campaign campaign,
        IReadOnlyList<EventPhoto> fresh, CancellationToken ct)
    {
        var uploaders = fresh.Select(p => p.GuestId).OfType<Guid>().ToHashSet();
        var onlyUploader = uploaders.Count == 1 ? uploaders.Single() : (Guid?)null;

        var guests = await db.Guests
            .Where(g => g.CampaignId == campaign.Id && !g.OptedOut && g.Email != null && g.Email != "")
            .ToListAsync(ct);

        // Never tell someone about photos that are all their own. Someone who contributed one of
        // twelve still hears about the other eleven, so this only excludes the sole uploader.
        var recipients = guests
            .Where(g => onlyUploader is not { } solo || g.Id != solo)
            .Select(g => g.Email!)
            .ToList();

        var suppressed = await SuppressedAsync(db, recipients, ct);
        recipients = recipients.Where(e => !suppressed.Contains(TokenHashOf(e))).Distinct().ToList();

        var count = fresh.Count;
        var title = string.IsNullOrWhiteSpace(campaign.Title) ? "the event" : campaign.Title;
        var subject = count == 1 ? $"A new photo from {title}" : $"{count} new photos from {title}";

        var guestLink = $"{Base("Urls:InviteeBase", "https://me.invites.blog")}/e/{campaign.Id}";
        var sentCount = 0;

        foreach (var address in recipients)
        {
            var result = await email.SendAsync(address, subject, Body(title, count, guestLink), ct);
            if (result.Success) sentCount++;
        }

        // The host too, at their own link — they manage the campaign rather than being on its list.
        var inviter = campaign.InviterId is { } id ? await db.Inviters.FindAsync([id], ct) : null;
        if (!string.IsNullOrWhiteSpace(inviter?.Email))
        {
            var hostLink = $"{Base("Urls:InviterBase", "https://invites.blog")}/dashboard/{campaign.Id}";
            var result = await email.SendAsync(inviter.Email!, subject, Body(title, count, hostLink), ct);
            if (result.Success) sentCount++;
        }

        return sentCount;
    }

    /// <summary>People who asked to be forgotten (§15.3). Matched on the same hash the list stores.</summary>
    private static async Task<HashSet<string>> SuppressedAsync(
        AppDbContext db, IReadOnlyList<string> addresses, CancellationToken ct)
    {
        var hashes = addresses.Select(TokenHashOf).Distinct().ToList();
        return (await db.SuppressionList
                .Where(s => hashes.Contains(s.ContactHash))
                .Select(s => s.ContactHash)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static string TokenHashOf(string email) =>
        Application.Security.TokenService.HashContact(email);

    private string Base(string key, string fallback) =>
        (config[key] ?? fallback).TrimEnd('/');

    private static string Body(string title, int count, string link) =>
        EmailLayout.Wrap(
            EmailLayout.Heading(count == 1 ? "A new photo" : $"{count} new photos") +
            EmailLayout.Paragraph(
                $"Someone added {(count == 1 ? "a photo" : $"{count} photos")} to " +
                $"<strong>{System.Net.WebUtility.HtmlEncode(title)}</strong>.") +
            $"""
             <div style="margin:26px 0 4px;text-align:center;">
               <a href="{System.Net.WebUtility.HtmlEncode(link)}"
                  style="display:inline-block;background:#d8b25a;color:#241d06;text-decoration:none;
                         font-family:-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif;
                         font-size:15px;font-weight:700;padding:14px 30px;border-radius:999px;">
                 See the photos
               </a>
             </div>
             """ +
            EmailLayout.Paragraph(
                "You're getting this because you were invited to this event. " +
                "Photos are collected in one place for everyone who was there.", muted: true) +
            EmailLayout.EndSpacer(),
            preheader: count == 1
                ? $"A new photo from {title}."
                : $"{count} new photos from {title}.");
}
