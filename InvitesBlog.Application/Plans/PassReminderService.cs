using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// "Your pass ends soon": a month before an event's Party or Wedding pass ends, then a week before,
/// the host is emailed an offer to extend it (a year more, without invitations). Each is sent once
/// (<see cref="Campaign.PassNoticeStage"/>); extending resets them.
/// </summary>
public interface IPassReminderService
{
    /// <summary>Sends whatever is due. Returns how many emails went out.</summary>
    Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken ct = default);
}

public sealed class PassReminderService(
    ICampaignRepository campaigns,
    IRepository<AppUser> users,
    IPassOfferService offers,
    IEmailSender email,
    IConfiguration config,
    IUnitOfWork uow) : IPassReminderService
{
    public static readonly TimeSpan MonthBefore = TimeSpan.FromDays(30);
    public static readonly TimeSpan WeekBefore = TimeSpan.FromDays(7);

    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var soon = now + MonthBefore;
        var due = await campaigns.Query(tracking: true)
            .Where(c => c.EventPass != EventPassKind.None && c.EventPassUntil != null
                        && c.EventPassUntil > now && c.EventPassUntil <= soon
                        && c.Status != CampaignStatus.Cancelled && c.Status != CampaignStatus.Draft
                        && c.PassNoticeStage < 2)
            .ToListAsync(ct);

        var sent = 0;
        foreach (var c in due)
        {
            var stage = c.EventPassUntil <= now + WeekBefore ? 2 : 1;
            if (c.PassNoticeStage >= stage) continue;

            var to = c.CreatedByUserId is { } hostId
                ? await users.Query().Where(u => u.Id == hostId).Select(u => u.Email).FirstOrDefaultAsync(ct)
                : null;
            // Marked either way: an event with nobody to tell shouldn't be looked at again every sweep.
            c.PassNoticeStage = stage;
            if (string.IsNullOrWhiteSpace(to)) continue;

            var offer = await offers.ForCampaignAsync(c.Id, ct);
            var passName = c.EventPass == EventPassKind.Wedding ? "Wedding pass" : "Party pass";
            await email.SendAsync(PassEmails.EndingSoon(to, c.Title, passName, c.EventPassUntil!.Value,
                offer.ExtensionPrice(c.EventPass), $"{config.InviterBase()}/dashboard/{c.Id}"), ct);
            sent++;
        }
        if (due.Count > 0) await uow.SaveChangesAsync(ct);
        return sent;
    }
}
