using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Application.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// How many guests invites.blog may still email for an event. Sharing links is always free; emailing each guest
/// their own link is what's counted, from three places, used in this order (owner, 2026-11-14):
/// <list type="number">
/// <item>the event's pass (100 with Gold, 500 with Platinum), each guest counted once, so sending again is free;</item>
/// <item>the organiser's monthly allowance (Premium 100, Photographer 500, Studio 500 a month, shared by all
/// their events, Maldives calendar month), where every email counts, sending again too;</item>
/// <item>emails bought for the event (or given by an admin), each guest counted once.</item>
/// </list>
/// </summary>
/// <param name="Included">What the event's pass includes.</param>
/// <param name="Extra">Bought for the event, or given by an admin (<c>Campaign.PaidInviteCapacity</c>).</param>
/// <param name="EventUsed">Guests paid for from the pass or bought emails. Each counts once.</param>
/// <param name="Monthly">The organiser's monthly allowance; 0 without an active subscription.</param>
/// <param name="MonthlyUsed">Emails taken from it this month, across all of the organiser's events.</param>
/// <param name="MonthlyResetsAt">When the monthly allowance starts again (the 1st, Maldives time); null without one.</param>
public sealed record SendingAllowanceDto(
    int Included, int Extra, int EventUsed, int Monthly = 0, int MonthlyUsed = 0, DateTimeOffset? MonthlyResetsAt = null)
{
    public int EventLeft => Math.Max(0, Included + Extra - EventUsed);
    public int MonthlyLeft => Math.Max(0, Monthly - MonthlyUsed);
    public int Total => Included + Extra + Monthly;
    public int Left => EventLeft + MonthlyLeft;
    /// <summary>Total minus Left: what older apps show as "emailed X of Y".</summary>
    public int Used => Total - Left;
}

/// <summary>The calendar month the monthly allowance counts in: Maldives time (UTC+5, no daylight saving).</summary>
public static class EmailMonth
{
    public static readonly TimeSpan Offset = TimeSpan.FromHours(5);

    /// <summary>yyyymm of the month <paramref name="at"/> falls in, in Maldives time.</summary>
    public static int Of(DateTimeOffset at)
    {
        var local = at.ToOffset(Offset);
        return local.Year * 100 + local.Month;
    }

    /// <summary>The start of the next month in Maldives time: when the allowance is whole again.</summary>
    public static DateTimeOffset ResetsAt(DateTimeOffset at)
    {
        var local = at.ToOffset(Offset);
        return new DateTimeOffset(local.Year, local.Month, 1, 0, 0, 0, Offset).AddMonths(1);
    }
}

public interface ISendingAllowanceService
{
    Task<SendingAllowanceDto> ForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>The same for many events in a fixed number of queries, given plans already worked out for them.</summary>
    Task<IReadOnlyDictionary<Guid, SendingAllowanceDto>> ForCampaignsAsync(
        IReadOnlyCollection<Campaign> list, IReadOnlyDictionary<Guid, EventPlan> plans, CancellationToken ct = default);
}

/// <summary>What paid for one email, so it can be given back if the email isn't sent.</summary>
/// <param name="PaidBy">Event or Monthly; null when it cost nothing (sending again to a guest the event paid for).</param>
/// <param name="LedgerId">The <see cref="MonthlyEmailSend"/> row, when the monthly allowance paid.</param>
/// <param name="PreviousFirstEmailedAt">The invite's mark before the charge.</param>
/// <param name="PreviousPaidBy">The invite's payer before the charge.</param>
public sealed record EmailCharge(
    EmailPaidBy? PaidBy, Guid? LedgerId, DateTimeOffset? PreviousFirstEmailedAt, EmailPaidBy PreviousPaidBy);

/// <summary>
/// Takes one email from an event's allowance before it is sent, and gives it back if it isn't. Charges for the
/// same organiser are taken one at a time (a database lock), so two sends at once can't go past the allowance.
/// </summary>
public interface IEmailAllowanceGate
{
    /// <summary>
    /// Charges emailing this guest to the first place with room: the event's pass, the organiser's month, emails
    /// bought for the event. Marks <paramref name="invite"/> (FirstEmailedAt, EmailPaidBy) and saves, adding the
    /// invite if it is new. Null when nothing has room: the email must not be sent.
    /// </summary>
    Task<EmailCharge?> ChargeAsync(Campaign campaign, Invite invite, CancellationToken ct = default);

    /// <summary>The email wasn't sent: give back what <see cref="ChargeAsync"/> took, and save.</summary>
    Task RefundAsync(EmailCharge charge, Invite invite, CancellationToken ct = default);
}

public sealed class SendingAllowanceService(
    ICampaignRepository campaigns,
    IInviteRepository invites,
    IRepository<AppUser> users,
    IRepository<MonthlyEmailSend> monthly,
    IPlanService plans,
    TimeProvider clock) : ISendingAllowanceService
{
    public async Task<SendingAllowanceDto> ForCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");
        var plan = await plans.ForCampaignAsync(campaignId, ct);
        var result = await ForCampaignsAsync([campaign], new Dictionary<Guid, EventPlan> { [campaignId] = plan }, ct);
        return result[campaignId];
    }

    public async Task<IReadOnlyDictionary<Guid, SendingAllowanceDto>> ForCampaignsAsync(
        IReadOnlyCollection<Campaign> list, IReadOnlyDictionary<Guid, EventPlan> plans, CancellationToken ct = default)
    {
        var ids = list.Select(c => c.Id).Distinct().ToList();
        var used = ids.Count == 0
            ? new Dictionary<Guid, int>()
            : await invites.Query()
                .Where(i => ids.Contains(i.CampaignId) && i.FirstEmailedAt != null && i.EmailPaidBy == EmailPaidBy.Event)
                .GroupBy(i => i.CampaignId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var now = clock.GetUtcNow();
        var month = EmailMonth.Of(now);
        var owners = ids.Select(id => plans[id].OwnerUserId).OfType<Guid>().Distinct().ToList();
        var allowance = owners.Count == 0
            ? new Dictionary<Guid, int>()
            : (await users.Query().Where(u => owners.Contains(u.Id))
                    .Select(u => new { u.Id, u.SubscriptionTier, u.SubscriptionEndsAt })
                    .ToListAsync(ct))
                .ToDictionary(u => u.Id,
                    u => PlanRules.IsActive(u.SubscriptionTier, u.SubscriptionEndsAt, now) ? PlanCatalog.MonthlyEmails(u.SubscriptionTier) : 0);
        var spent = owners.Count == 0
            ? new Dictionary<Guid, int>()
            : await monthly.Query()
                .Where(m => owners.Contains(m.AccountId) && m.Month == month)
                .GroupBy(m => m.AccountId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var result = new Dictionary<Guid, SendingAllowanceDto>(ids.Count);
        foreach (var c in list)
        {
            var plan = plans[c.Id];
            var mine = plan.OwnerUserId is { } o ? allowance.GetValueOrDefault(o) : 0;
            result[c.Id] = new SendingAllowanceDto(
                plan.IncludedInvites, Math.Max(0, c.PaidInviteCapacity), used.GetValueOrDefault(c.Id),
                mine, mine > 0 && plan.OwnerUserId is { } owner ? spent.GetValueOrDefault(owner) : 0,
                mine > 0 ? EmailMonth.ResetsAt(now) : null);
        }
        return result;
    }
}
