using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// Works out which plan covers an event: its pass, its organiser's Premium or Studio plan (tier Venue), and
/// anything bought to keep its photos.
/// </summary>
public interface IPlanService
{
    Task<EventPlan> ForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>
    /// The plans of many events at once, in a fixed number of queries (a list page, Billing): the same
    /// answer <see cref="ForCampaignAsync"/> gives for each, which is worked out through here too.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, EventPlan>> ForCampaignsAsync(IReadOnlyCollection<Campaign> list, CancellationToken ct = default);

    /// <summary>
    /// The badge for each event whose organiser is on Studio (tier Venue) right now: their display name and
    /// profile picture. Events without one are left out. Two queries at most, whatever the number of events.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, VenueFace>> VenueFacesAsync(IReadOnlyCollection<Campaign> list, CancellationToken ct = default);

    /// <summary>Whether this account's Premium subscription is in force.</summary>
    Task<bool> IsPremiumAsync(Guid userId, CancellationToken ct = default);
}

/// <summary>
/// The name and picture an event's albums, QR cards and posts carry while its organiser is on Studio: the
/// organiser's own display name and profile picture (served as venueName / venueLogoUrl).
/// </summary>
/// <param name="Kind">The organiser's plan as an event plan kind: "Venue" (Studio) or "Photographer".</param>
public sealed record VenueFace(string Name, string? LogoUrl, string Kind = "Venue");

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
            .Select(b => new AlbumRow(b.OwnerUserId, b.CreatedAt, b.Tier, b.TermEndAt))
            .ToListAsync(ct);
        var ownerId = OwnerOf(campaign, rows);

        // Found through the context (not a fresh query), so a change made earlier in the same request counts.
        (SubscriptionTier, DateTimeOffset?)? organiser = null;
        if (ownerId is { } owner && await users.GetByIdAsync(owner, ct) is { } o)
            organiser = (o.SubscriptionTier, o.SubscriptionEndsAt);

        return Evaluate(campaign, rows, ownerId, organiser, DateTimeOffset.UtcNow);
    }

    public async Task<IReadOnlyDictionary<Guid, EventPlan>> ForCampaignsAsync(IReadOnlyCollection<Campaign> list, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, EventPlan>(list.Count);
        if (list.Count == 0) return result;
        var ids = list.Select(c => c.Id).Distinct().ToList();

        // Two queries whatever the number of events: their albums, and the accounts that organised them.
        var albums = (await buckets.Query()
                .Where(b => ids.Contains(b.CampaignId))
                .Select(b => new { b.CampaignId, Row = new AlbumRow(b.OwnerUserId, b.CreatedAt, b.Tier, b.TermEndAt) })
                .ToListAsync(ct))
            .ToLookup(b => b.CampaignId, b => b.Row);
        var owners = new Dictionary<Guid, Guid?>();
        foreach (var c in list) owners[c.Id] = OwnerOf(c, albums[c.Id].ToList());

        var userIds = owners.Values.OfType<Guid>().Distinct().ToList();
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
            (SubscriptionTier, DateTimeOffset?)? organiser =
                ownerId is { } o && accounts.TryGetValue(o, out var oa) ? oa : null;
            result[campaign.Id] = Evaluate(campaign, albums[campaign.Id].ToList(), ownerId, organiser, now);
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, VenueFace>> VenueFacesAsync(IReadOnlyCollection<Campaign> list, CancellationToken ct = default)
    {
        var result = new Dictionary<Guid, VenueFace>();
        if (list.Count == 0) return result;

        // Whose plan the event counts, as for its limits: whoever started it, else whoever made its first album.
        // Only events with no recorded starter need their albums looked at.
        var unknown = list.Where(c => c.CreatedByUserId is null || c.CreatedByUserId == Guid.Empty)
            .Select(c => c.Id).Distinct().ToList();
        var firstAlbumOwners = unknown.Count == 0
            ? new Dictionary<Guid, Guid>()
            : (await buckets.Query()
                    .Where(b => unknown.Contains(b.CampaignId))
                    .Select(b => new { b.CampaignId, b.OwnerUserId, b.CreatedAt })
                    .ToListAsync(ct))
                .GroupBy(b => b.CampaignId)
                .ToDictionary(g => g.Key, g => g.OrderBy(b => b.CreatedAt).First().OwnerUserId);

        var owners = new Dictionary<Guid, Guid>();
        foreach (var c in list)
        {
            Guid? id = c.CreatedByUserId is { } by && by != Guid.Empty ? by
                : firstAlbumOwners.TryGetValue(c.Id, out var first) ? first : null;
            if (id is { } g && g != Guid.Empty) owners[c.Id] = g;
        }
        if (owners.Count == 0) return result;

        var userIds = owners.Values.Distinct().ToList();
        var now = DateTimeOffset.UtcNow;
        var faces = (await users.Query()
                .Where(u => userIds.Contains(u.Id)
                            && (u.SubscriptionTier == SubscriptionTier.Venue || u.SubscriptionTier == SubscriptionTier.Photographer))
                .Select(u => new { u.Id, u.DisplayName, u.AvatarUrl, u.SubscriptionTier, u.SubscriptionEndsAt })
                .ToListAsync(ct))
            .Where(u => PlanRules.IsActive(u.SubscriptionTier, u.SubscriptionEndsAt, now))
            .ToDictionary(u => u.Id, u => new VenueFace(u.DisplayName, u.AvatarUrl,
                u.SubscriptionTier == SubscriptionTier.Photographer ? nameof(PlanKind.Photographer) : nameof(PlanKind.Venue)));

        foreach (var (campaignId, ownerId) in owners)
            if (faces.TryGetValue(ownerId, out var face)) result[campaignId] = face;
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
    /// <param name="organiser">The plan of the account the event counts as organised by.</param>
    private static EventPlan Evaluate(
        Campaign campaign, IReadOnlyList<AlbumRow> rows, Guid? ownerId,
        (SubscriptionTier Tier, DateTimeOffset? EndsAt)? organiser,
        DateTimeOffset now)
    {
        // The organiser's Studio plan (tier Venue) lifts every event they organise while it lasts, the way
        // Premium does. Once it has ended, the photos' lapse counts from when it did. An organiser since moved
        // to another plan is on that plan instead (the admin switch, or a new purchase).
        // Photographer (owner, 2026-11-13) does the same, only the event's kind says Photographer.
        (bool, DateTimeOffset?)? venue = null;
        var photographer = organiser is { Tier: SubscriptionTier.Photographer };
        if (organiser is { } v && SubscriptionTiers.IsStudioClass(v.Tier))
        {
            var active = PlanRules.IsActive(v.Tier, v.EndsAt, now);
            venue = (active, active ? null : v.EndsAt);
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
            campaign.MediaTrimmedAt,
            photographer);
    }

    public async Task<bool> IsPremiumAsync(Guid userId, CancellationToken ct = default) =>
        await users.GetByIdAsync(userId, ct) is { SubscriptionTier: SubscriptionTier.Premium } user
        && PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, DateTimeOffset.UtcNow);

}
