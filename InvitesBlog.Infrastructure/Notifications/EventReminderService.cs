using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.Application.Events;
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
/// The day before an event, reminds each guest who said they're coming (or might) on their phone: once per
/// invitation (<see cref="Invite.ReminderPushedAt"/>), within <see cref="Ahead"/> of the start and not in its
/// last <see cref="TooLate"/>. The time is the event's own, with its zone, as the emails write it.
///
/// <para>Organisers aren't reminded of their own event. Save the dates, cancelled and unfinished events are
/// skipped. Registered in the API host only. On unless <c>Notifications:Reminders:Enabled</c> is false; each
/// person can mute it in the app (kind <see cref="PushKinds.Reminder"/>).</para>
/// </summary>
public sealed class EventReminderService(
    IServiceProvider services,
    IConfiguration config,
    ILogger<EventReminderService> logger) : BackgroundService
{
    public static readonly TimeSpan Ahead = TimeSpan.FromHours(24);
    public static readonly TimeSpan TooLate = TimeSpan.FromHours(1);
    private static readonly TimeSpan SweepEvery = TimeSpan.FromMinutes(15);

    private bool Enabled => config.GetValue("Notifications:Reminders:Enabled", true);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (Enabled)
                {
                    using var scope = services.CreateScope();
                    var sp = scope.ServiceProvider;
                    var sent = await SweepAsync(sp.GetRequiredService<AppDbContext>(),
                        sp.GetRequiredService<PushNotificationService>(),
                        sp.GetRequiredService<IPushSender>(), DateTimeOffset.UtcNow, stoppingToken);
                    if (sent > 0) logger.LogInformation("Event reminders sent: {Count}.", sent);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Event reminder sweep failed.");
            }
            await Task.Delay(SweepEvery, stoppingToken);
        }
    }

    /// <summary>One sweep at <paramref name="now"/>. Returns how many notifications went out.</summary>
    public static async Task<int> SweepAsync(
        AppDbContext db, PushNotificationService people, IPushSender sender, DateTimeOffset now, CancellationToken ct)
    {
        var from = now + TooLate;
        var to = now + Ahead;
        var hidden = GuestVisibility.Hidden;
        var campaigns = await db.Campaigns
            .Where(c => c.EventStartAt > from && c.EventStartAt <= to
                        && c.Status != CampaignStatus.Cancelled && !hidden.Contains(c.Status)
                        && c.Kind != CampaignKind.SaveTheDate)
            .Where(c => db.Invites.Any(i => i.CampaignId == c.Id && i.ReminderPushedAt == null
                                            && (i.RsvpStatus == RsvpStatus.Going || i.RsvpStatus == RsvpStatus.Maybe)))
            .ToListAsync(ct);

        var sent = 0;
        foreach (var campaign in campaigns)
        {
            var due = await db.Invites
                .Where(i => i.CampaignId == campaign.Id && i.ReminderPushedAt == null
                            && (i.RsvpStatus == RsvpStatus.Going || i.RsvpStatus == RsvpStatus.Maybe))
                .Join(db.Guests.Where(g => !g.OptedOut), i => i.GuestId, g => g.Id, (i, g) => new { Invite = i.Id, g.Email, g.PhoneE164 })
                .ToListAsync(ct);
            var ids = await db.Invites.Where(i => i.CampaignId == campaign.Id && i.ReminderPushedAt == null
                                                  && (i.RsvpStatus == RsvpStatus.Going || i.RsvpStatus == RsvpStatus.Maybe))
                .Select(i => i.Id).ToListAsync(ct);
            // Marked first, whether or not anyone has the app: each invitation is looked at once.
            await db.Invites.Where(i => ids.Contains(i.Id)).ExecuteUpdateAsync(s => s.SetProperty(i => i.ReminderPushedAt, now), ct);

            var organisers = (await people.OrganisersAsync(campaign, ct)).ToHashSet();
            var body = Body(campaign, now);
            var told = new HashSet<Guid>();
            foreach (var guest in due)
                foreach (var userId in await people.AccountsAsync(guest.Email, guest.PhoneE164, ct))
                {
                    if (organisers.Contains(userId) || !told.Add(userId)) continue;
                    await sender.SendAsync(userId, campaign.Title, body,
                        PushKinds.Data($"/invitation/{campaign.Id}", PushKinds.Reminder, campaign.Id.ToString(), $"reminder-{campaign.Id:N}"), ct);
                    sent++;
                }
        }
        return sent;
    }

    /// <summary>"Tomorrow at 7:00 pm +05." / "Today." for an all-day event: the day and time on the event's clock.</summary>
    public static string Body(Campaign campaign, DateTimeOffset now)
    {
        var today = EventTime.Day(now, campaign.TimeZone);
        var day = EventTime.Day(campaign.EventStartAt, campaign.TimeZone) == today ? "Today" : "Tomorrow";
        return campaign.AllDay ? $"{day}." : $"{day} at {EventTime.TimeText(campaign.EventStartAt, campaign.TimeZone)}.";
    }
}
