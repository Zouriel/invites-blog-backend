using InvitesBlog.Application.Events;
using InvitesBlog.Application.Pricing;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// What an event is on right now. A Platinum pass outranks a Gold pass, which outranks the organiser's
/// Studio plan (<see cref="Venue"/>, the enum name kept for stored values and shipped apps), which outranks
/// Premium (also the organiser's subscription), which outranks Free. With a pass and Studio together, the
/// event shows the pass but gets the best of both, limit by limit.
/// </summary>
public enum PlanKind
{
    Free,
    GoldPass,
    PlatinumPass,
    Venue,
    Premium,
    /// <summary>The organiser's Photographer plan: like <see cref="Venue"/> in every limit (owner, 2026-11-13).</summary>
    Photographer,
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
/// event its subscriber organises to 2 GB, without the mark, kept while it lasts. Studio (shown as
/// "Studio", kind and tier "Venue" internally) is an account plan for businesses that run events: a monthly
/// subscription that does the same for every event its subscriber organises, with 10 GB each, and puts their
/// picture and name on those events (owner, 2026-11-12); a pass on top adds what it has more of. Studio
/// accounts pay normal pass prices. The template designer is open to every account. Prices are in rufiyaa,
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
    /// <summary>Each event organised by an account whose Studio plan (tier Venue) is in force.</summary>
    public const long VenueEventBytes = 10 * Gb;

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
    /// Emailed invitations a subscription includes each calendar month (Maldives time), shared by every event the
    /// account organises and not carried over (owner, 2026-11-14; Studio capped at 500 the same day). Every email
    /// counts, sending again too. An event uses its pass's own first, then these, then emails bought for it.
    /// </summary>
    public const int PremiumMonthlyEmails = 100;
    public const int PhotographerMonthlyEmails = 500;
    public const int VenueMonthlyEmails = 500;

    /// <summary>The monthly emailed invitations a subscription tier includes; 0 for none.</summary>
    public static int MonthlyEmails(SubscriptionTier tier) => tier switch
    {
        SubscriptionTier.Premium => PremiumMonthlyEmails,
        SubscriptionTier.Photographer => PhotographerMonthlyEmails,
        SubscriptionTier.Venue => VenueMonthlyEmails,
        _ => 0,
    };

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
    /// <summary>Premium a month (owner, 2026-11-13: 77, was 200; the AI designer became an add-on).</summary>
    public const decimal PremiumMonthly = 77m;

    /// <summary>The AI designer add-on to Premium, a month (owner, 2026-11-13). Studio has it included.</summary>
    public const decimal DesignerAiMonthly = 77m;

    /// <summary>How many photos anyone may download or share at once without "Download all" (pricing 2026-11).</summary>
    public const int DownloadLimit = DownloadRules.Limit;

    /// <summary>A Studio account (tier Venue), a month (owner, 2026-11-12: about $50).</summary>
    public const decimal VenueMonthly = 775m;

    /// <summary>A Photographer account, a month (owner, 2026-11-13: $35; 35 x 15.42 = 539.7, so 540).</summary>
    public const decimal PhotographerMonthly = 540m;

    /// <summary>
    /// No longer a discount (owner, 2026-11-12): venues pay normal pass prices. Still served as 0 where
    /// older pages read it.
    /// </summary>
    public const int VenuePassDiscountPercent = 0;

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
                    FreeEventBytes, FreeBuckets, FreeWindowDays, null, 0, false, true,
                    Perks: PlanPerks.For(PlanKind.Free)),
                new PlanDto("GoldPass", "Gold pass", p.GoldPass, "per event",
                    GoldEventBytes, GoldBuckets, GoldWindowDays, 365, GoldIncludedInvites, false, false,
                    ExtensionPrice: p.GoldExtension, Perks: PlanPerks.For(PlanKind.GoldPass)),
                new PlanDto("PlatinumPass", "Platinum pass", p.PlatinumPass, "per event",
                    PlatinumEventBytes, PlatinumBuckets, PlatinumWindowDays, 365, PlatinumIncludedInvites, true, false,
                    ExtensionPrice: p.PlatinumExtension, Perks: PlanPerks.For(PlanKind.PlatinumPass)),
                // Every event its subscriber organises, kept while it lasts (no retention days). The AI designer
                // is an add-on (AddOns below), not one of its perks.
                new PlanDto("Premium", "Premium", p.PremiumMonthly, "per month",
                    PremiumEventBytes, FreeBuckets, FreeWindowDays, null, 0, false, false,
                    Perks: PlanPerks.For(PlanKind.Premium), MonthlyEmails: PremiumMonthlyEmails),
                // Studio without the AI designer (owner, 2026-11-13), between Premium and Studio.
                new PlanDto("Photographer", "Photographer", p.PhotographerMonthly, "per month",
                    VenueEventBytes, FreeBuckets, FreeWindowDays, null, 0, false, false,
                    Perks: PlanPerks.For(PlanKind.Photographer), MonthlyEmails: PhotographerMonthlyEmails),
                // Every event its subscriber organises, kept while it lasts (no retention days), like Premium with 10 GB.
                // Shown as "Studio" (owner, 2026-11-12); the kind stays "Venue" for shipped apps and stored data.
                new PlanDto("Venue", "Studio", p.VenueMonthly, "per month",
                    VenueEventBytes, FreeBuckets, FreeWindowDays, null, 0, false, false,
                    Perks: PlanPerks.For(PlanKind.Venue), MonthlyEmails: VenueMonthlyEmails),
            ],
            new KeepPhotosDto(p.KeepPhotosYearly, KeepPhotosMonths),
            new SendingPriceDto(p.SendingPerBlock, PricingCalculator.BlockSize),
            new LapseDto(ReminderDay, OrganiserOnlyDay, FinalNoticeDay, DeleteDay),
            // The venue discount is gone: always 0, whatever an old saved price list says.
            VenuePassDiscountPercent,
            AddOns.Describe(p),
            DownloadLimit);
    }
}

/// <summary>One plan as the pricing page shows it.</summary>
/// <param name="ExtensionPrice">Another year of this pass, without invitations; null for the rest.</param>
/// <param name="RetentionDays">How long photos are kept from the event day; null when they are kept for good (Free) or while a subscription covers them.</param>
/// <param name="EventBytes">Space for each event; for Premium and Studio (Venue), each event its subscriber organises.</param>
/// <param name="IncludedInvites">Invitations sent for the event without charge.</param>
/// <param name="PrivateAlbums">Whether an album can be closed to some guests.</param>
/// <param name="Branded">Whether the invitation and album carry a small "Made with invites.blog".</param>
/// <param name="From">The price is the smallest; larger ones are quoted.</param>
/// <param name="Perks">The card's bullets, written here so the website and both apps show the same list (<see cref="PlanPerks"/>).</param>
/// <param name="MonthlyEmails">Emailed invitations a subscription includes each month across its events; 0 for passes and Free.</param>
public sealed record PlanDto(
    string Kind, string Name, decimal Price, string Billing,
    long? EventBytes, int? MaxBuckets, int? MaxWindowDays, int? RetentionDays,
    int IncludedInvites, bool PrivateAlbums, bool Branded, bool From = false, decimal? ExtensionPrice = null,
    IReadOnlyList<string>? Perks = null, int MonthlyEmails = 0);

/// <summary>"Keep your photos": how much a year, and how many months each one adds.</summary>
public sealed record KeepPhotosDto(decimal Price, int Months);

/// <summary>Invitations invites.blog sends beyond what a pass includes: <c>PerBlock</c> for every <c>BlockSize</c>.</summary>
public sealed record SendingPriceDto(decimal PerBlock, int BlockSize);

public sealed record LapseDto(int ReminderDay, int OrganiserOnlyDay, int FinalNoticeDay, int DeleteDay);

/// <param name="AddOns">What can be added to a plan (today the AI designer, to Premium).</param>
/// <param name="DownloadLimit">How many photos may be downloaded or shared at once without "Download all".</param>
public sealed record PlanCatalogDto(
    string Currency, decimal MvrPerUsd, IReadOnlyList<PlanDto> Plans, KeepPhotosDto KeepPhotos,
    SendingPriceDto Sending, LapseDto Lapse, int VenueDiscountPercent = PlanCatalog.VenuePassDiscountPercent,
    IReadOnlyList<AddOnDto>? AddOns = null, int DownloadLimit = DownloadRules.Limit);

/// <summary>What one event may do right now, and how long it is covered for.</summary>
/// <param name="AccountBytes">No longer set by any plan (it was a venue's space across its events); always null.</param>
/// <param name="IncludedInvites">Invitations invites.blog sends for it without charging.</param>
/// <param name="CoveredUntil">When its cover ends; null when it never does: a free event (one that never had a pass), or while its organiser's Premium (with no end) or Studio plan covers it.</param>
/// <param name="OwnerUserId">Whoever organised the event: the one told when its photos are ending.</param>
/// <param name="VenueAccountId">The organiser, when their Studio plan (tier Venue) covers the event right now: whose picture and name its albums carry.</param>
/// <param name="VenueEnded">The organiser's Studio plan covered it and has ended: a pass is what keeps its photos now.</param>
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
    Guid? VenueAccountId = null,
    bool VenueEnded = false);

/// <summary>The rules, with no database in them, so every branch can be tested directly.</summary>
public static class PlanRules
{
    /// <summary>
    /// "Group photos by face" comes with every paid plan: a Gold or Platinum pass, Premium, or Studio (owner,
    /// 2026-11-11). A Free event doesn't get it, and an event whose pass has ended loses it (FaceGroupingService).
    /// </summary>
    public static bool IncludesFaceGrouping(EventPlan plan) => plan.Kind != PlanKind.Free;

    public static bool IsActive(SubscriptionTier tier, DateTimeOffset? endsAt, DateTimeOffset now) =>
        tier != SubscriptionTier.None && (endsAt is null || endsAt > now);

    /// <summary>When a pass bought or given today stops covering the event: a year from the event, or from today if it has passed.</summary>
    public static DateTimeOffset PassUntil(DateTimeOffset eventDate, DateTimeOffset now) =>
        (eventDate > now ? eventDate : now).AddMonths(PlanCatalog.PassMonths);

    /// <param name="venue">
    /// The organiser's Studio plan (tier Venue), if they have or had one: whether it is in force, and when
    /// it ended (null while active). While it lasts the event gets
    /// a plan like Premium with <see cref="PlanCatalog.VenueEventBytes"/> and no end date; with a pass
    /// as well, the best of both, limit by limit. Once it ends the event goes back to Free like an ended
    /// Premium: uploads close, then the warnings and the cut (owner, 2026-11-12).
    /// </param>
    /// <param name="photographer">The <paramref name="venue"/> plan is Photographer, not Studio: the same in every
    /// limit, only the event's kind differs so it is labelled right.</param>
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
        (bool Active, DateTimeOffset? EndedAt)? venue,
        DateTimeOffset? legacyCoverUntil,
        DateTimeOffset? mediaDeletedAt,
        Guid? ownerUserId,
        (bool Active, DateTimeOffset? EndsAt)? premium = null,
        DateTimeOffset? trimmedAt = null,
        bool photographer = false)
    {
        var passActive = pass != EventPassKind.None && passUntil is { } until && until > now;
        var premiumActive = premium is { Active: true };
        var venueActive = venue is { Active: true };

        // The pass shows when there is one (it adds the most); its limits are raised to the venue's below.
        var kind = passActive && pass == EventPassKind.Platinum ? PlanKind.PlatinumPass
            : passActive && pass == EventPassKind.Gold ? PlanKind.GoldPass
            : venueActive ? (photographer ? PlanKind.Photographer : PlanKind.Venue)
            : premiumActive ? PlanKind.Premium
            : PlanKind.Free;

        var eventBytes = kind switch
        {
            PlanKind.GoldPass => PlanCatalog.GoldEventBytes,
            PlanKind.PlatinumPass => PlanCatalog.PlatinumEventBytes,
            PlanKind.Venue or PlanKind.Photographer => PlanCatalog.VenueEventBytes,
            PlanKind.Premium => PlanCatalog.PremiumEventBytes,
            _ => PlanCatalog.FreeEventBytes,
        };
        // Best of both: a pass on a venue event never gives it less space than the venue does.
        if (venueActive && eventBytes < PlanCatalog.VenueEventBytes) eventBytes = PlanCatalog.VenueEventBytes;
        // The plan is the only source of an event's space. Albums from before the plans once kept
        // their old size, which made an event on Free show and allow more than Free, while its
        // settings said a fixed size. Only their cover date is still honoured (legacyCoverUntil).

        // For a pass event, the cover is the latest of 90 days from the event, its pass, "Keep your photos", the end of
        // its venue's plan, the organiser's Premium, and what anything from
        // before the plans was promised — so ending one never shortens another.
        var end = eventDate.AddDays(PlanCatalog.FreeCoverDays);
        foreach (var candidate in new[] { pass != EventPassKind.None ? passUntil : null, keepPhotosUntil, venue?.EndedAt, legacyCoverUntil, premium?.EndsAt })
            if (candidate is { } c && c > end) end = c;
        // Free events are kept for good, as on any social platform (owner, 2026-11-03): an event that
        // never had a pass or its organiser's Premium has no end date, so no warnings and no removal.
        // One that had either is covered to `end`; after that it goes back to Free, and once it has
        // been cut down to Free's space (trimmedAt) it is a free event like any other, kept for good,
        // until something new covers it past the cut. Premium with no end has no end date either.
        // A venue counts once it has covered the event: in force now, or ended on a known date.
        var everPaid = pass != EventPassKind.None || premium is not null || venue is { Active: true } or { EndedAt: not null };
        DateTimeOffset? coveredUntil =
            !everPaid ? null
            : premium is { Active: true, EndsAt: null } ? null
            // Kept while the venue's plan lasts, like Premium with no end: its renewals move no date.
            : venueActive ? null
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
            ownerUserId,
            venueActive ? ownerUserId : null,
            venue is { Active: false, EndedAt: not null });
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
