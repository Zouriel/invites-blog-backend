using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Plans;

/// <summary>Works out which plan covers an event: its pass, its venue, and anything bought to keep its photos.</summary>
public interface IPlanService
{
    Task<EventPlan> ForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>Space used by every event at a venue, which the venue's plan counts together.</summary>
    Task<long> VenueUsedBytesAsync(Guid venueId, CancellationToken ct = default);

    /// <summary>Whether this account's Studio plan is in force.</summary>
    Task<bool> IsStudioAsync(Guid userId, CancellationToken ct = default);

    PlanCatalogDto Catalog();
}

public sealed class PlanService(
    ICampaignRepository campaigns,
    IRepository<AppUser> users,
    IRepository<Venue> venues,
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

        // The venue's plan is its owner's: in force while they are on Venue.
        (Guid, bool, DateTimeOffset?)? venue = null;
        if (campaign.VenueId is { } venueId && await venues.GetByIdAsync(venueId, ct) is { } place
            && await users.GetByIdAsync(place.OwnerUserId, ct) is { } venueOwner)
        {
            var active = venueOwner.SubscriptionTier == SubscriptionTier.Venue
                         && PlanRules.IsActive(venueOwner.SubscriptionTier, venueOwner.SubscriptionEndsAt, DateTimeOffset.UtcNow);
            // Once it has ended, the photos' lapse counts from when it did (ending a plan sets that date).
            venue = (venueId, active, active ? null : venueOwner.SubscriptionEndsAt);
        }

        // Anything from before the plans keeps what it had, and is never removed sooner than 90 days
        // after the plans arrived, so nobody loses photos the day this ships.
        var legacy = rows.Where(b => b.CreatedAt < PlanCatalog.IntroducedAt).ToList();
        DateTimeOffset? legacyCover = null;
        if (legacy.Count > 0 || campaign.CreatedAt < PlanCatalog.IntroducedAt)
        {
            legacyCover = PlanCatalog.IntroducedAt.AddDays(PlanCatalog.FreeCoverDays);
            foreach (var paid in legacy.Where(b => b.Tier != MediaBucketTier.Free))
            {
                var end = campaign.EventStartAt.AddMonths(PlanCatalog.LegacyMonths);
                if (paid.TermEndAt is { } term && term > end) end = term;
                if (end > legacyCover) legacyCover = end;
            }
        }

        return PlanRules.Evaluate(
            DateTimeOffset.UtcNow,
            campaign.EventStartAt,
            campaign.EventPass,
            campaign.EventPassUntil,
            campaign.KeepPhotosUntil,
            venue,
            legacy.Sum(b => b.CapacityBytes),
            legacyCover,
            campaign.MediaDeletedAt,
            ownerId is { } id && id != Guid.Empty ? id : null);
    }

    public async Task<long> VenueUsedBytesAsync(Guid venueId, CancellationToken ct = default)
    {
        var events = campaigns.Query().Where(c => c.VenueId == venueId).Select(c => c.Id);
        return await buckets.Query()
            .Where(b => events.Contains(b.CampaignId))
            .SumAsync(b => b.UsedBytes, ct);
    }

    public async Task<bool> IsStudioAsync(Guid userId, CancellationToken ct = default) =>
        await users.GetByIdAsync(userId, ct) is { SubscriptionTier: SubscriptionTier.Studio } user
        && PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, DateTimeOffset.UtcNow);

    public PlanCatalogDto Catalog() => PlanCatalog.Describe();
}
