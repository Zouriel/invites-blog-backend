using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Plans;

/// <summary>Works out which plan covers an event: its pass, its venue's plan, its organiser's Premium, and anything bought to keep its photos.</summary>
public interface IPlanService
{
    Task<EventPlan> ForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>
    /// The plans of many events at once, in a fixed number of queries (a list page, Billing): the same
    /// answer <see cref="ForCampaignAsync"/> gives for each, which is worked out through here too.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, EventPlan>> ForCampaignsAsync(IReadOnlyCollection<Campaign> list, CancellationToken ct = default);

    /// <summary>Space used by every event at a venue, for its page. Each event's own space is its plan's.</summary>
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
            .Select(b => new AlbumRow(b.OwnerUserId, b.CreatedAt, b.Tier, b.TermEndAt))
            .ToListAsync(ct);
        var ownerId = OwnerOf(campaign, rows);

        // Found through the context (not a fresh query), so a change made earlier in the same request counts.
        (SubscriptionTier, DateTimeOffset?)? venueOwner = null;
        if (campaign.VenueId is { } venueId && await venues.GetByIdAsync(venueId, ct) is { } place
            && await users.GetByIdAsync(place.OwnerUserId, ct) is { } vo)
            venueOwner = (vo.SubscriptionTier, vo.SubscriptionEndsAt);
        (SubscriptionTier, DateTimeOffset?)? organiser = null;
        if (ownerId is { } owner && await users.GetByIdAsync(owner, ct) is { } o)
            organiser = (o.SubscriptionTier, o.SubscriptionEndsAt);

        return Evaluate(campaign, rows, ownerId, venueOwner, organiser, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyDictionary<Guid, EventPlan>> ForCampaignsAsync(IReadOnlyCollection<Campaign> list, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, EventPlan>(list.Count);
        if (list.Count == 0) return result;
        var ids = list.Select(c => c.Id).Distinct().ToList();

        // Three queries whatever the number of events: their albums, their venues, and the accounts behind both.
        var albums = (await buckets.Query()
                .Where(b => ids.Contains(b.CampaignId))
                .Select(b => new { b.CampaignId, Row = new AlbumRow(b.OwnerUserId, b.CreatedAt, b.Tier, b.TermEndAt) })
                .ToListAsync(ct))
            .ToLookup(b => b.CampaignId, b => b.Row);
        var owners = new Dictionary<Guid, Guid?>();
        foreach (var c in list) owners[c.Id] = OwnerOf(c, albums[c.Id].ToList());

        var venueIds = list.Select(c => c.VenueId).OfType<Guid>().Distinct().ToList();
        var places = venueIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await venues.Query().Where(v => venueIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, v => v.OwnerUserId, ct);

        var userIds = owners.Values.OfType<Guid>().Concat(places.Values).Distinct().ToList();
        var accounts = userIds.Count == 0
            ? new Dictionary<Guid, (SubscriptionTier, DateTimeOffset?)>()
            : (await users.Query().Where(u => userIds.Contains(u.Id))
                    .Select(u => new { u.Id, u.SubscriptionTier, u.SubscriptionEndsAt })
                    .ToListAsync(ct))
                .ToDictionary(u => u.Id, u => (u.SubscriptionTier, u.SubscriptionEndsAt));

        var now = DateTimeOffset.UtcNow;
        foreach (var campaign in list)
        {
            if (result.ContainsKey(campaign.Id)) continue;
            var ownerId = owners[campaign.Id];
            (SubscriptionTier, DateTimeOffset?)? venueOwner =
                campaign.VenueId is { } v && places.TryGetValue(v, out var vo) && accounts.TryGetValue(vo, out var va) ? va : null;
            (SubscriptionTier, DateTimeOffset?)? organiser =
                ownerId is { } o && accounts.TryGetValue(o, out var oa) ? oa : null;
            result[campaign.Id] = Evaluate(campaign, albums[campaign.Id].ToList(), ownerId, venueOwner, organiser, now);
        }
        return result;
    }

    private sealed record AlbumRow(Guid OwnerUserId, DateTimeOffset CreatedAt, MediaBucketTier Tier, DateTimeOffset? TermEndAt);

    /// <summary>Whose plan the event counts: whoever started it, else whoever made its first album.</summary>
    private static Guid? OwnerOf(Campaign campaign, IReadOnlyList<AlbumRow> rows)
    {
        var id = campaign.CreatedByUserId ?? rows.OrderBy(b => b.CreatedAt).Select(b => (Guid?)b.OwnerUserId).FirstOrDefault();
        return id is { } g && g != Guid.Empty ? g : null;
    }

    /// <summary>The one place an event's plan is worked out from what was loaded, singly or for a page.</summary>
    /// <param name="venueOwner">The plan of the account that owns the event's venue, if it has one.</param>
    /// <param name="organiser">The plan of the account the event counts as organised by.</param>
    private static EventPlan Evaluate(
        Campaign campaign, IReadOnlyList<AlbumRow> rows, Guid? ownerId,
        (SubscriptionTier Tier, DateTimeOffset? EndsAt)? venueOwner,
        (SubscriptionTier Tier, DateTimeOffset? EndsAt)? organiser,
        DateTimeOffset now)
    {
        // The venue's plan is its owner's: in force while they are on Venue.
        (Guid, bool, DateTimeOffset?)? venue = null;
        if (campaign.VenueId is { } venueId && venueOwner is { } vo)
        {
            var active = vo.Tier == SubscriptionTier.Venue && PlanRules.IsActive(vo.Tier, vo.EndsAt, now);
            // Once it has ended, the photos' lapse counts from when it did (ending a plan sets that date).
            // An owner since moved to another plan has that plan's date on the account, not the venue's:
            // no known end, so the venue no longer counts (the event is whatever else it has).
            DateTimeOffset? ended = active ? null
                : vo.Tier is SubscriptionTier.Venue or SubscriptionTier.None ? vo.EndsAt
                : null;
            venue = (venueId, active, ended);
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
        (bool, DateTimeOffset?)? premium = organiser is { Tier: SubscriptionTier.Premium } p
            ? (PlanRules.IsActive(p.Tier, p.EndsAt, now), p.EndsAt)
            : null;

        return PlanRules.Evaluate(
            now,
            campaign.EventStartAt,
            campaign.EventPass,
            campaign.EventPassUntil,
            campaign.KeepPhotosUntil,
            venue,
            legacyCover,
            campaign.MediaDeletedAt,
            ownerId,
            premium,
            campaign.MediaTrimmedAt);
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
