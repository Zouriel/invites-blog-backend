using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Plans;

/// <summary>Works out which plan covers an event: its pass, its organiser's Premium, and anything bought to keep its photos.</summary>
public interface IPlanService
{
    Task<EventPlan> ForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>Space used by every event at a venue, which the venue's plan counts together.</summary>
    Task<long> VenueUsedBytesAsync(Guid venueId, CancellationToken ct = default);

    /// <summary>Whether this account's Premium subscription is in force.</summary>
    Task<bool> IsPremiumAsync(Guid userId, CancellationToken ct = default);
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
            .Select(b => new { b.OwnerUserId, b.CreatedAt, b.Tier, b.TermEndAt })
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

        // Anything from before the plans is never removed sooner than 90 days after the plans arrived,
        // so nobody loses photos the day this ships. Its space is the plan's, like everything else.
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

        // The organiser's Premium lifts every event they organise while it lasts.
        (bool, DateTimeOffset?)? premium = null;
        if (ownerId is { } owner && owner != Guid.Empty
            && await users.GetByIdAsync(owner, ct) is { SubscriptionTier: SubscriptionTier.Premium } organiser)
            premium = (PlanRules.IsActive(organiser.SubscriptionTier, organiser.SubscriptionEndsAt, DateTimeOffset.UtcNow),
                       organiser.SubscriptionEndsAt);

        return PlanRules.Evaluate(
            DateTimeOffset.UtcNow,
            campaign.EventStartAt,
            campaign.EventPass,
            campaign.EventPassUntil,
            campaign.KeepPhotosUntil,
            venue,
            legacyCover,
            campaign.MediaDeletedAt,
            ownerId is { } id && id != Guid.Empty ? id : null,
            premium);
    }

    public async Task<long> VenueUsedBytesAsync(Guid venueId, CancellationToken ct = default)
    {
        var events = campaigns.Query().Where(c => c.VenueId == venueId).Select(c => c.Id);
        return await buckets.Query()
            .Where(b => events.Contains(b.CampaignId))
            .SumAsync(b => b.UsedBytes, ct);
    }

    public async Task<bool> IsPremiumAsync(Guid userId, CancellationToken ct = default) =>
        await users.GetByIdAsync(userId, ct) is { SubscriptionTier: SubscriptionTier.Premium } user
        && PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, DateTimeOffset.UtcNow);

}
