using InvitesBlog.Application.Events;
using InvitesBlog.Application.Pricing;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// What an event is on right now. A venue outranks a Wedding pass, which outranks a Party pass.
/// Studio is an ACCOUNT plan and changes nothing an event may do, so it isn't one of these.
/// </summary>
public enum PlanKind
{
    Free,
    PartyPass,
    WeddingPass,
    Venue,
}

/// <summary>
/// Where an event's photos are after its cover runs out. See <see cref="PlanCatalog"/> for the days.
/// </summary>
public enum MediaPhase
{
    /// <summary>Covered. Guests can add and look.</summary>
    Active,
    /// <summary>Days 0–29 after the cover ended: nothing new can be added, guests can still look.</summary>
    UploadsClosed,
    /// <summary>Days 30–89: only the organiser can look and download.</summary>
    OrganiserOnly,
    /// <summary>Day 90 onwards: the photos are removed.</summary>
    Deleted,
}

/// <summary>
/// The plans, their limits and their prices. One place, read by the limits the server enforces and by
/// the pricing page, so the two can't disagree.
///
/// <para>Hosts pay per event, because most have one big day rather than a monthly need: Free for
/// everything small, a Party pass, a Wedding pass. Subscriptions are for professionals only: Studio for
/// designers and planners, Venue for resorts and halls. Prices are in rufiyaa, shown with dollars
/// alongside.</para>
/// </summary>
public static class PlanCatalog
{
    public const long Mb = 1024L * 1024;
    public const long Gb = 1024L * Mb;

    public const long FreeEventBytes = 1 * Gb;
    public const long PartyEventBytes = 10 * Gb;
    public const long WeddingEventBytes = 100 * Gb;
    public const long VenueEventBytes = 100 * Gb;

    /// <summary>Everything a venue's events hold together.</summary>
    public const long VenueAccountBytes = 1024 * Gb;

    public const int FreeBuckets = 1;
    public const int PartyBuckets = 2;
    public const int WeddingBuckets = MediaBucket.MaxPerCampaign;

    public const int FreeWindowDays = 1;
    public const int PartyWindowDays = 3;
    public const int WeddingWindowDays = EventDayWindow.MaxWindowDays;

    /// <summary>Invitations invites.blog sends for the event without charging for them.</summary>
    public const int PartyIncludedInvites = 100;
    public const int WeddingIncludedInvites = 500;

    /// <summary>How long a free event is covered, counted from the event day.</summary>
    public const int FreeCoverDays = 90;

    /// <summary>How long a pass covers its event, counted from the event day.</summary>
    public const int PassMonths = 12;

    /// <summary>What one "Keep your photos" buys.</summary>
    public const int KeepPhotosMonths = 12;

    /// <summary>Days after the cover ends.</summary>
    public const int ReminderDay = 23;
    public const int OrganiserOnlyDay = 30;
    public const int FinalNoticeDay = 83;
    public const int DeleteDay = 90;

    public const string Currency = "MVR";

    /// <summary>Rufiyaa to the dollar, for the approximate dollar prices shown alongside.</summary>
    public const decimal MvrPerUsd = 15.42m;

    // Default prices. What is charged comes from IPriceBook, which an admin can change; these are
    // what it starts from and falls back to.
    public const decimal PartyPassPrice = 199m;
    public const decimal WeddingPassPrice = 699m;
    public const decimal KeepPhotosYearly = 150m;
    public const decimal StudioMonthly = 450m;
    public const decimal StudioYearly = 4500m;

    /// <summary>The smallest venue's price; larger properties are quoted.</summary>
    public const decimal VenueMonthlyFrom = 2300m;

    /// <summary>What a Studio account pays for a pass it gives to a client: 30% off.</summary>
    public const int StudioPassDiscountPercent = 30;

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
                new PlanDto("Free", "Free", 0m, "every event", null, null,
                    FreeEventBytes, null, FreeBuckets, FreeWindowDays, FreeCoverDays, 0, false, true),
                new PlanDto("PartyPass", "Party pass", p.PartyPass, "per event", null, p.StudioPassPrice(EventPassKind.Party),
                    PartyEventBytes, null, PartyBuckets, PartyWindowDays, 365, PartyIncludedInvites, false, false),
                new PlanDto("WeddingPass", "Wedding pass", p.WeddingPass, "per event", null, p.StudioPassPrice(EventPassKind.Wedding),
                    WeddingEventBytes, null, WeddingBuckets, WeddingWindowDays, 365, WeddingIncludedInvites, true, false),
                new PlanDto("Studio", "Studio", p.StudioMonthly, "per month", p.StudioYearly, null,
                    null, null, null, null, null, 0, false, false),
                new PlanDto("Venue", "Venue", p.VenueMonthlyFrom, "per month", null, null,
                    VenueEventBytes, VenueAccountBytes, WeddingBuckets, WeddingWindowDays, null, 0, true, false, From: true),
            ],
            new KeepPhotosDto(p.KeepPhotosYearly, KeepPhotosMonths),
            new SendingPriceDto(p.SendingPerBlock, PricingCalculator.BlockSize),
            new LapseDto(ReminderDay, OrganiserOnlyDay, FinalNoticeDay, DeleteDay),
            p.StudioDiscountPercent);
    }
}

/// <summary>One plan as the pricing page shows it. Studio has no event limits of its own: nulls.</summary>
/// <param name="StudioPrice">What a Studio account pays for this pass to give a client; null for the rest.</param>
/// <param name="RetentionDays">How long photos are kept from the event day; null while a subscription covers them.</param>
/// <param name="IncludedInvites">Invitations sent for the event without charge.</param>
/// <param name="PrivateAlbums">Whether an album can be closed to some guests.</param>
/// <param name="Branded">Whether the invitation and album carry a small "Made with invites.blog".</param>
/// <param name="From">The price is the smallest; larger ones are quoted.</param>
public sealed record PlanDto(
    string Kind, string Name, decimal Price, string Billing, decimal? YearlyPrice, decimal? StudioPrice,
    long? EventBytes, long? AccountBytes, int? MaxBuckets, int? MaxWindowDays, int? RetentionDays,
    int IncludedInvites, bool PrivateAlbums, bool Branded, bool From = false);

/// <summary>"Keep your photos": how much a year, and how many months each one adds.</summary>
public sealed record KeepPhotosDto(decimal Price, int Months);

/// <summary>Invitations invites.blog sends beyond what a pass includes: <c>PerBlock</c> for every <c>BlockSize</c>.</summary>
public sealed record SendingPriceDto(decimal PerBlock, int BlockSize);

public sealed record LapseDto(int ReminderDay, int OrganiserOnlyDay, int FinalNoticeDay, int DeleteDay);

public sealed record PlanCatalogDto(
    string Currency, decimal MvrPerUsd, IReadOnlyList<PlanDto> Plans, KeepPhotosDto KeepPhotos,
    SendingPriceDto Sending, LapseDto Lapse, int StudioDiscountPercent);

/// <summary>What one event may do right now, and how long it is covered for.</summary>
/// <param name="AccountBytes">A venue's space across all of its events; null for everything else.</param>
/// <param name="IncludedInvites">Invitations invites.blog sends for it without charging.</param>
/// <param name="CoveredUntil">When its cover ends; null while a venue's plan covers it.</param>
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

    /// <param name="venue">The venue the event is at, if any: its id, whether its plan is in force, and when it ended.</param>
    /// <param name="legacyBytes">Space held by buckets made before <see cref="PlanCatalog.IntroducedAt"/>.</param>
    /// <param name="legacyCoverUntil">The latest date anything from before the plans is covered to.</param>
    public static EventPlan Evaluate(
        DateTimeOffset now,
        DateTimeOffset eventDate,
        EventPassKind pass,
        DateTimeOffset? passUntil,
        DateTimeOffset? keepPhotosUntil,
        (Guid Id, bool Active, DateTimeOffset? EndedAt)? venue,
        long legacyBytes,
        DateTimeOffset? legacyCoverUntil,
        DateTimeOffset? mediaDeletedAt,
        Guid? ownerUserId)
    {
        var atVenue = venue is { Active: true };
        var passActive = pass != EventPassKind.None && passUntil is { } until && until > now;

        var kind = atVenue ? PlanKind.Venue
            : passActive && pass == EventPassKind.Wedding ? PlanKind.WeddingPass
            : passActive && pass == EventPassKind.Party ? PlanKind.PartyPass
            : PlanKind.Free;

        var eventBytes = kind switch
        {
            PlanKind.PartyPass => PlanCatalog.PartyEventBytes,
            PlanKind.WeddingPass => PlanCatalog.WeddingEventBytes,
            PlanKind.Venue => PlanCatalog.VenueEventBytes,
            _ => PlanCatalog.FreeEventBytes,
        };
        // Buckets made before the plans keep their space until six months after their event.
        if (legacyBytes > eventBytes && now < eventDate.AddMonths(PlanCatalog.LegacyMonths))
            eventBytes = legacyBytes;

        // A venue's plan covers its events while it runs. Otherwise the cover is the latest of the
        // free 90 days, a pass, "Keep your photos", a venue that has since ended, and what anything
        // from before the plans was promised — so ending one never shortens another.
        DateTimeOffset? coveredUntil = null;
        if (!atVenue)
        {
            var end = eventDate.AddDays(PlanCatalog.FreeCoverDays);
            foreach (var candidate in new[] { pass != EventPassKind.None ? passUntil : null, keepPhotosUntil, venue?.EndedAt, legacyCoverUntil })
                if (candidate is { } c && c > end) end = c;
            coveredUntil = end;
        }

        var large = kind is PlanKind.WeddingPass or PlanKind.Venue;
        return new EventPlan(
            kind,
            eventBytes,
            atVenue ? PlanCatalog.VenueAccountBytes : null,
            large ? PlanCatalog.WeddingBuckets : kind == PlanKind.PartyPass ? PlanCatalog.PartyBuckets : PlanCatalog.FreeBuckets,
            large ? PlanCatalog.WeddingWindowDays : kind == PlanKind.PartyPass ? PlanCatalog.PartyWindowDays : PlanCatalog.FreeWindowDays,
            kind switch
            {
                PlanKind.WeddingPass => PlanCatalog.WeddingIncludedInvites,
                PlanKind.PartyPass => PlanCatalog.PartyIncludedInvites,
                _ => 0,
            },
            large,
            kind == PlanKind.Free,
            coveredUntil,
            PhaseOf(now, coveredUntil, mediaDeletedAt),
            ownerUserId,
            atVenue ? venue!.Value.Id : null);
    }

    public static MediaPhase PhaseOf(DateTimeOffset now, DateTimeOffset? coveredUntil, DateTimeOffset? deletedAt)
    {
        if (deletedAt is not null) return MediaPhase.Deleted;
        if (coveredUntil is not { } end || now < end) return MediaPhase.Active;
        var days = (now - end).TotalDays;
        return days < PlanCatalog.OrganiserOnlyDay ? MediaPhase.UploadsClosed
            : days < PlanCatalog.DeleteDay ? MediaPhase.OrganiserOnly
            : MediaPhase.Deleted;
    }
}
