using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Plans;

/// <summary>Works out which plan covers an event, from its organiser's subscription and its pass.</summary>
public interface IPlanService
{
    Task<EventPlan> ForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>
    /// Space used by an account's events that its subscription pays for. Events with their own pass
    /// don't count toward it.
    /// </summary>
    Task<long> AccountUsedBytesAsync(Guid ownerUserId, CancellationToken ct = default);

    PlanCatalogDto Catalog();
}

public sealed class PlanService(
    ICampaignRepository campaigns,
    IRepository<AppUser> users,
    IRepository<MediaBucket> buckets) : IPlanService
{
    public async Task<EventPlan> ForCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");

        var rows = await buckets.Query()
            .Where(b => b.CampaignId == campaignId)
            .Select(b => new { b.OwnerUserId, b.CreatedAt, b.CapacityBytes, b.Tier, b.TermEndAt })
            .ToListAsync(ct);

        var ownerId = campaign.CreatedByUserId
                      ?? rows.OrderBy(b => b.CreatedAt).Select(b => (Guid?)b.OwnerUserId).FirstOrDefault();
        var owner = ownerId is { } id && id != Guid.Empty ? await users.GetByIdAsync(id, ct) : null;

        // Anything from before the plans keeps what it had, and is never removed sooner than 90 days
        // after the plans arrived, so nobody loses photos the day this ships.
        var legacy = rows.Where(b => b.CreatedAt < PlanCatalog.IntroducedAt).ToList();
        DateTimeOffset? legacyCover = null;
        if (legacy.Count > 0 || campaign.CreatedAt < PlanCatalog.IntroducedAt)
        {
            legacyCover = PlanCatalog.IntroducedAt.AddDays(PlanCatalog.FreeCoverDays);
            foreach (var paid in legacy.Where(b => b.Tier != MediaBucketTier.Free))
            {
                var end = campaign.EventStartAt.AddMonths(PlanCatalog.EventPassMonths);
                if (paid.TermEndAt is { } term && term > end) end = term;
                if (end > legacyCover) legacyCover = end;
            }
        }

        return PlanRules.Evaluate(
            DateTimeOffset.UtcNow,
            campaign.EventStartAt,
            owner?.SubscriptionTier ?? SubscriptionTier.None,
            owner?.SubscriptionEndsAt,
            campaign.EventPassUntil,
            legacy.Sum(b => b.CapacityBytes),
            legacyCover,
            campaign.MediaDeletedAt,
            owner?.Id ?? ownerId);
    }

    public async Task<long> AccountUsedBytesAsync(Guid ownerUserId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var passed = await campaigns.Query()
            .Where(c => c.EventPassUntil != null && c.EventPassUntil > now)
            .Select(c => c.Id)
            .ToListAsync(ct);

        return await buckets.Query()
            .Where(b => b.OwnerUserId == ownerUserId && !passed.Contains(b.CampaignId))
            .SumAsync(b => b.UsedBytes, ct);
    }

    public PlanCatalogDto Catalog() => PlanCatalog.Describe();
}
