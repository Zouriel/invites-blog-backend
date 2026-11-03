using InvitesBlog.Application.Events;
using InvitesBlog.Application.Pricing;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// What an event is on right now. A Platinum pass outranks a Gold pass, which outranks Premium (its
/// organiser's subscription), which outranks Free.
/// </summary>
public enum PlanKind
{
    Free,
    GoldPass,
    PlatinumPass,
    Venue,
    Premium,
}

/// <summary>
/// Where an event's photos are after its pass runs out. See <see cref="PlanCatalog"/> for the days.
/// A free event never leaves <see cref="Active"/>: it has no end.
/// </summary>
public enum MediaPhase
{
    /// <summary>Covered. Guests can add and look.</summary>
    Active,
    /// <summary>
    /// The pass (or Premium) has ended and the event hasn't been cut down to Free yet: nothing new can
    /// be added, everyone can still look, and the organiser is being warned.
    /// </summary>
    UploadsClosed,
    /// <summary>No longer produced (a lapse used to make photos organiser-only); kept for stored values.</summary>
    OrganiserOnly,
    /// <summary>The photos are gone: the event was cancelled.</summary>
    Deleted,
}

/// <summary>
/// The plans, their limits and their prices. One place, read by the limits the server enforces and by
/// the pricing page, so the two can't disagree.
///
/// <para>Hosts pay per event, because most have one big day rather than a monthly need: Free for
/// everything small, a Gold pass, a Platinum pass. Premium is a monthly subscription that lifts every
/// event its subscriber organises to 2 GB, without the mark, kept while it lasts. Venue (resorts and
/// halls) is a monthly subscription: a venue buys and renews passes for the events it runs at the
/// venue discount, and charges its clients itself. The template designer is open to every account. Prices are in rufiyaa,
/// shown with dollars alongside.</para>
/// </summary>
public static class PlanCatalog
{
    public const long Mb = 1024L * 1024;
    public const long Gb = 1024L * Mb;

    public const long FreeEventBytes = 300 * Mb;
    public const long PremiumEventBytes = 2 * Gb;
    public const long GoldEventBytes = 5 * Gb;
    public const long PlatinumEventBytes = 10 * Gb;

    public const int FreeBuckets = 1;
    public const int GoldBuckets = 2;
    public const int PlatinumBuckets = MediaBucket.MaxPerCampaign;

    public const int FreeWindowDays = 1;
    public const int GoldWindowDays = 3;
    public const int PlatinumWindowDays = EventDayWindow.MaxWindowDays;

    /// <summary>Invitations invites.blog sends for the event without charging for them.</summary>
    public const int GoldIncludedInvites = 100;
    public const int PlatinumIncludedInvites = 500;

    /// <summary>
    /// The least a pass event is covered, counted from the event day (a pass's own year always runs
    /// longer). Free events are no longer covered by days at all: they are kept for good.
    /// </summary>
    public const int FreeCoverDays = 90;

    /// <summary>How long a pass covers its event, counted from the event day.</summary>
    public const int PassMonths = 12;

    /// <summary>What one "Keep your photos" buys.</summary>
    public const int KeepPhotosMonths = 12;

    /// <summary>
    /// After a pass (or Premium) ends: the first warning gives at least this many days before anything
    /// above Free's space is removed, then one a week before and one a day before.
    /// </summary>
    public const int TrimNoticeDays = 30;
    public const int TrimReminderDaysBefore = 7;
    public const int TrimFinalDaysBefore = 1;

    // The old lapse (days after the cover ended), still served in /api/plans for older app builds.
    public const int ReminderDay = 23;
    public const int OrganiserOnlyDay = 30;
    public const int FinalNoticeDay = 83;
    public const int DeleteDay = 90;

    public const string Currency = "MVR";

    /// <summary>Rufiyaa to the dollar, for the approximate dollar prices shown alongside.</summary>
    public const decimal MvrPerUsd = 15.42m;

    // Default prices. What is charged comes from IPriceBook, which an admin can change; these are
    // what it starts from and falls back to.
    public const decimal GoldPassPrice = 199m;
    public const decimal PlatinumPassPrice = 499m;
    public const decimal KeepPhotosYearly = 150m;
    /// <summary>Another year of a pass, without invitations: half the pass.</summary>
    public const decimal GoldExtension = 99m;
    public const decimal PlatinumExtension = 349m;
    public const decimal PremiumMonthly = 200m;

    /// <summary>A venue account, a month.</summary>
    public const decimal VenueMonthly = 2300m;

    /// <summary>What a venue pays for a pass, or another year of one, for an event it runs: half.</summary>
    public const int VenuePassDiscountPercent = 50;

    /// <summary>
    /// When per-event plans replaced per-bucket sizes. Anything made before it keeps the space it had
    /// until six months after its event, and isn't removed before 90 days after this date.
    /// </summary>
    public static readonly DateTimeOffset IntroducedAt = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    /// <summary>How long a bucket made before <see cref="IntroducedAt"/> keeps the space it had, from its event.</summary>
    public const int LegacyMonths = 6;

    /// <summary>The catalog as /api/plans serves it: limits from here, prices from the price book.</summary>
    public static PlanCatalogDto Describe(Prices? prices = null)
    {
        var p = prices ?? Prices.Defaults;
        return new(
            Currency,
            p.MvrPerUsd,
            [
                // Kept for good: no retention days.
                new PlanDto("Free", "Free", 0m, "every event",
                    FreeEventBytes, FreeBuckets, FreeWindowDays, null, 0, false, true),
                new PlanDto("GoldPass", "Gold pass", p.GoldPass, "per event",
                    GoldEventBytes, GoldBuckets, GoldWindowDays, 365, GoldIncludedInvites, false, false,
                    ExtensionPrice: p.GoldExtension),
                new PlanDto("PlatinumPass", "Platinum pass", p.PlatinumPass, "per event",
                    PlatinumEventBytes, PlatinumBuckets, PlatinumWindowDays, 365, PlatinumIncludedInvites, true, false,
                    ExtensionPrice: p.PlatinumExtension),
                // Every event its subscriber organises, kept while it lasts (no retention days).
                new PlanDto("Premium", "Premium pass", p.PremiumMonthly, "per month",
                    PremiumEventBytes, FreeBuckets, FreeWindowDays, null, 0, false, false),
                // What it offers is the discount on its events' passes, and the designer.
                new PlanDto("Venue", "Venue", p.VenueMonthly, "per month",
                    null, null, null, null, 0, false, false),
            ],
            new KeepPhotosDto(p.KeepPhotosYearly, KeepPhotosMonths),
            new SendingPriceDto(p.SendingPerBlock, PricingCalculator.BlockSize),
            new LapseDto(ReminderDay, OrganiserOnlyDay, FinalNoticeDay, DeleteDay),
            p.VenueDiscountPercent);
    }
}

/// <summary>One plan as the pricing page shows it. Venue has no event limits of its own: nulls.</summary>
/// <param name="ExtensionPrice">Another year of this pass, without invitations; null for the rest.</param>
/// <param name="RetentionDays">How long photos are kept from the event day; null when they are kept for good (Free) or while a subscription covers them.</param>
/// <param name="EventBytes">Space for each event; for Premium, each event its subscriber organises.</param>
/// <param name="IncludedInvites">Invitations sent for the event without charge.</param>
/// <param name="PrivateAlbums">Whether an album can be closed to some guests.</param>
/// <param name="Branded">Whether the invitation and album carry a small "Made with invites.blog".</param>
/// <param name="From">The price is the smallest; larger ones are quoted.</param>
public sealed record PlanDto(
    string Kind, string Name, decimal Price, string Billing,
    long? EventBytes, int? MaxBuckets, int? MaxWindowDays, int? RetentionDays,
    int IncludedInvites, bool PrivateAlbums, bool Branded, bool From = false, decimal? ExtensionPrice = null);

/// <summary>"Keep your photos": how much a year, and how many months each one adds.</summary>
public sealed record KeepPhotosDto(decimal Price, int Months);

/// <summary>Invitations invites.blog sends beyond what a pass includes: <c>PerBlock</c> for every <c>BlockSize</c>.</summary>
public sealed record SendingPriceDto(decimal PerBlock, int BlockSize);

public sealed record LapseDto(int ReminderDay, int OrganiserOnlyDay, int FinalNoticeDay, int DeleteDay);

public sealed record PlanCatalogDto(
    string Currency, decimal MvrPerUsd, IReadOnlyList<PlanDto> Plans, KeepPhotosDto KeepPhotos,
    SendingPriceDto Sending, LapseDto Lapse, int VenueDiscountPercent = PlanCatalog.VenuePassDiscountPercent);

/// <summary>What one event may do right now, and how long it is covered for.</summary>
/// <param name="AccountBytes">A venue's space across all of its events; null for everything else.</param>
/// <param name="IncludedInvites">Invitations invites.blog sends for it without charging.</param>
/// <param name="CoveredUntil">When its cover ends; null when it never does: a free event (one that never had a pass), or while its organiser's Premium (with no end) covers it.</param>
/// <param name="OwnerUserId">Whoever organised the event: the one told when its photos are ending.</param>
/// <param name="VenueId">The venue whose plan covers it, when one does. Its space is counted across the venue.</param>
public sealed record EventPlan(
    PlanKind Kind,
    long EventBytes,
    long? AccountBytes,
    int MaxBuckets,
    int MaxWindowDays,
    int IncludedInvites,
    bool PrivateAlbums,
    bool Branded,
    DateTimeOffset? CoveredUntil,
    MediaPhase Phase,
    Guid? OwnerUserId,
    Guid? VenueId = null);

/// <summary>The rules, with no database in them, so every branch can be tested directly.</summary>
public static class PlanRules
{
    public static bool IsActive(SubscriptionTier tier, DateTimeOffset? endsAt, DateTimeOffset now) =>
        tier != SubscriptionTier.None && (endsAt is null || endsAt > now);

    /// <summary>When a pass bought or given today stops covering the event: a year from the event, or from today if it has passed.</summary>
    public static DateTimeOffset PassUntil(DateTimeOffset eventDate, DateTimeOffset now) =>
        (eventDate > now ? eventDate : now).AddMonths(PlanCatalog.PassMonths);

    /// <param name="venue">
    /// The venue the event is at, if any. It no longer covers the event: since 2026-09-27 a venue buys
    /// passes for its events (at a discount) like anyone else, so its events are Free or on a pass.
    /// Kept for its end date, which still never shortens a cover.
    /// </param>
    /// <param name="legacyCoverUntil">The latest date anything from before the plans is covered to.</param>
    /// <param name="trimmedAt">When the event was cut down to Free's space after its last cover ended, if it was.</param>
    /// <param name="premium">
    /// The organiser's Premium subscription, if they have or had one: whether it is in force and when it
    /// ends (null: no end). While it lasts their events get Premium's space and keep their photos; once it
    /// ends, the photos' lapse counts from its end.
    /// </param>
    public static EventPlan Evaluate(
        DateTimeOffset now,
        DateTimeOffset eventDate,
        EventPassKind pass,
        DateTimeOffset? passUntil,
        DateTimeOffset? keepPhotosUntil,
        (Guid Id, bool Active, DateTimeOffset? EndedAt)? venue,
        DateTimeOffset? legacyCoverUntil,
        DateTimeOffset? mediaDeletedAt,
        Guid? ownerUserId,
        (bool Active, DateTimeOffset? EndsAt)? premium = null,
        DateTimeOffset? trimmedAt = null)
    {
        var passActive = pass != EventPassKind.None && passUntil is { } until && until > now;
        var premiumActive = premium is { Active: true };

        var kind = passActive && pass == EventPassKind.Platinum ? PlanKind.PlatinumPass
            : passActive && pass == EventPassKind.Gold ? PlanKind.GoldPass
            : premiumActive ? PlanKind.Premium
            : PlanKind.Free;

        var eventBytes = kind switch
        {
            PlanKind.GoldPass => PlanCatalog.GoldEventBytes,
            PlanKind.PlatinumPass => PlanCatalog.PlatinumEventBytes,
            PlanKind.Premium => PlanCatalog.PremiumEventBytes,
            _ => PlanCatalog.FreeEventBytes,
        };
        // The plan is the only source of an event's space. Albums from before the plans once kept
        // their old size, which made an event on Free show and allow more than Free, while its
        // settings said a fixed size. Only their cover date is still honoured (legacyCoverUntil).

        // For a pass event, the cover is the latest of 90 days from the event, its pass, "Keep your photos", a venue plan that
        // covered it before venues bought passes, the organiser's Premium, and what anything from
        // before the plans was promised — so ending one never shortens another.
        var end = eventDate.AddDays(PlanCatalog.FreeCoverDays);
        foreach (var candidate in new[] { pass != EventPassKind.None ? passUntil : null, keepPhotosUntil, venue?.EndedAt, legacyCoverUntil, premium?.EndsAt })
            if (candidate is { } c && c > end) end = c;
        // Free events are kept for good, as on any social platform (owner, 2026-11-03): an event that
        // never had a pass or its organiser's Premium has no end date, so no warnings and no removal.
        // One that had either is covered to `end`; after that it goes back to Free, and once it has
        // been cut down to Free's space (trimmedAt) it is a free event like any other, kept for good,
        // until something new covers it past the cut. Premium with no end has no end date either.
        var everPaid = pass != EventPassKind.None || premium is not null;
        DateTimeOffset? coveredUntil =
            !everPaid ? null
            : premium is { Active: true, EndsAt: null } ? null
            : trimmedAt is { } cut && cut >= end ? null
            : end;

        var large = kind is PlanKind.PlatinumPass;
        return new EventPlan(
            kind,
            eventBytes,
            null,
            large ? PlanCatalog.PlatinumBuckets : kind == PlanKind.GoldPass ? PlanCatalog.GoldBuckets : PlanCatalog.FreeBuckets,
            large ? PlanCatalog.PlatinumWindowDays : kind == PlanKind.GoldPass ? PlanCatalog.GoldWindowDays : PlanCatalog.FreeWindowDays,
            kind switch
            {
                PlanKind.PlatinumPass => PlanCatalog.PlatinumIncludedInvites,
                PlanKind.GoldPass => PlanCatalog.GoldIncludedInvites,
                _ => 0,
            },
            large,
            kind == PlanKind.Free,
            coveredUntil,
            PhaseOf(now, coveredUntil, mediaDeletedAt),
            ownerUserId);
    }

    public static MediaPhase PhaseOf(DateTimeOffset now, DateTimeOffset? coveredUntil, DateTimeOffset? deletedAt)
    {
        if (deletedAt is not null) return MediaPhase.Deleted;
        if (coveredUntil is not { } end || now < end) return MediaPhase.Active;
        // Ended and not yet cut down to Free: everyone can still look; nothing new can be added.
        // Photos are never all removed for a lapse any more (owner, 2026-11-03), so the old
        // organiser-only and deleted phases only come from a cancelled event.
        return MediaPhase.UploadsClosed;
    }
}
