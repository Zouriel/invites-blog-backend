using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Application.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// How many guests invites.blog may still email for an event. Sharing links is always free; emailing
/// each guest their own link is what's counted: the event's pass includes some (none on Free, 100
/// with a Gold pass, 500 with a Platinum pass), and more are added a block at a time.
/// </summary>
/// <param name="Included">What the event's plan includes.</param>
/// <param name="Extra">Added on top: bought, or given by an admin (<c>Campaign.PaidInviteCapacity</c>).</param>
/// <param name="Used">Guests emailed so far. Each counts once; a resend is free.</param>
public sealed record SendingAllowanceDto(int Included, int Extra, int Used)
{
    public int Total => Included + Extra;
    public int Left => Math.Max(0, Total - Used);
}

public interface ISendingAllowanceService
{
    Task<SendingAllowanceDto> ForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>The same for many events in one query, given plans already worked out for them.</summary>
    Task<IReadOnlyDictionary<Guid, SendingAllowanceDto>> ForCampaignsAsync(
        IReadOnlyCollection<Campaign> list, IReadOnlyDictionary<Guid, EventPlan> plans, CancellationToken ct = default);
}

public sealed class SendingAllowanceService(
    ICampaignRepository campaigns,
    IInviteRepository invites,
    IPlanService plans) : ISendingAllowanceService
{
    public async Task<SendingAllowanceDto> ForCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");
        var plan = await plans.ForCampaignAsync(campaignId, ct);
        var used = await invites.Query().CountAsync(i => i.CampaignId == campaignId && i.FirstEmailedAt != null, ct);
        return new SendingAllowanceDto(plan.IncludedInvites, Math.Max(0, campaign.PaidInviteCapacity), used);
    }

    public async Task<IReadOnlyDictionary<Guid, SendingAllowanceDto>> ForCampaignsAsync(
        IReadOnlyCollection<Campaign> list, IReadOnlyDictionary<Guid, EventPlan> plans, CancellationToken ct = default)
    {
        var ids = list.Select(c => c.Id).Distinct().ToList();
        var used = ids.Count == 0
            ? new Dictionary<Guid, int>()
            : await invites.Query()
                .Where(i => ids.Contains(i.CampaignId) && i.FirstEmailedAt != null)
                .GroupBy(i => i.CampaignId)
                .Select(g => new { g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var result = new Dictionary<Guid, SendingAllowanceDto>(ids.Count);
        foreach (var c in list)
            result[c.Id] = new SendingAllowanceDto(plans[c.Id].IncludedInvites, Math.Max(0, c.PaidInviteCapacity),
                used.GetValueOrDefault(c.Id));
        return result;
    }
}
