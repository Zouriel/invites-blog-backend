using InvitesBlog.Application.Events;
using InvitesBlog.Application.Pricing;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Plans;

/// <summary>What an event is on right now. Premium outranks a pass, a pass outranks Basic.</summary>
public enum PlanKind
{
    Free,
    Basic,
    EventPass,
    Premium,
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
/// The plans and their limits. One place, read by the limits the server enforces and by the pricing
/// page, so the two can't disagree.
/// </summary>
public static class PlanCatalog
{
    public const long Mb = 1024L * 1024;
    public const long Gb = 1024L * Mb;

    public const long FreeEventBytes = 500 * Mb;
    public const long BasicEventBytes = 2 * Gb;
    public const long LargeEventBytes = 50 * Gb;

    public const long BasicAccountBytes = 20 * Gb;
    public const long PremiumAccountBytes = 200 * Gb;

    /// <summary>What a new bucket starts with on a subscription, before its owner resizes it.</summary>
    public const long BasicBucketBytes = 2 * Gb;
    public const long PremiumBucketBytes = 10 * Gb;

    /// <summary>The most one event can be given on a subscription, across its buckets.</summary>
    public const long BasicEventMaxBytes = 10 * Gb;
    public const long PremiumEventMaxBytes = LargeEventBytes;

    public static long EventMaxBytes(PlanKind kind) =>
        kind == PlanKind.Premium ? PremiumEventMaxBytes : kind == PlanKind.Basic ? BasicEventMaxBytes : 0;

    /// <summary>How long a free event is covered, counted from the event day.</summary>
    public const int FreeCoverDays = 90;

    public const int EventPassMonths = 6;

    /// <summary>Days after the cover ends.</summary>
    public const int ReminderDay = 23;
    public const int OrganiserOnlyDay = 30;
    public const int FinalNoticeDay = 83;
    public const int DeleteDay = 90;

    public const string Currency = "USD";
    public const decimal BasicYearly = 12m;
    public const decimal PremiumMonthly = 9m;
    public const decimal PremiumYearly = 79m;
    public const decimal EventPass = 19m;

    /// <summary>
    /// When these plans replaced per-bucket sizes. Anything made before it keeps the space it had
    /// until six months after its event, and isn't removed before 90 days after this date.
    /// </summary>
    public static readonly DateTimeOffset IntroducedAt = new(2026, 9, 15, 0, 0, 0, TimeSpan.Zero);

    public static PlanCatalogDto Describe() => new(
        Currency,
        [
            new PlanDto("Free", "Free", 0m, "Every event", null,
                FreeEventBytes, null, 1, 1, FreeCoverDays, false, PricingCalculator.StandardBlockSize),
            new PlanDto("Basic", "Basic", BasicYearly, "per year", null,
                BasicEventBytes, BasicAccountBytes, 1, 1, null, false, PricingCalculator.StandardBlockSize,
                true, BasicBucketBytes),
            new PlanDto("EventPass", "Event pass", EventPass, "once, for one event", null,
                LargeEventBytes, null, MediaBucket.MaxPerCampaign, EventDayWindow.MaxWindowDays,
                EventPassMonths * 30, true, PricingCalculator.StandardBlockSize),
            new PlanDto("Premium", "Premium", PremiumMonthly, "per month", PremiumYearly,
                LargeEventBytes, PremiumAccountBytes, MediaBucket.MaxPerCampaign, EventDayWindow.MaxWindowDays,
                null, false, PricingCalculator.DesignerBlockSize, true, PremiumBucketBytes),
        ],
        new SendingPriceDto(
            PricingCalculator.MinimumPrice, PricingCalculator.IncludedInvites, PricingCalculator.PricePerBlock,
            PricingCalculator.StandardBlockSize, PricingCalculator.DesignerBlockSize),
        new LapseDto(ReminderDay, OrganiserOnlyDay, FinalNoticeDay, DeleteDay));
}

/// <summary>One plan as the pricing page shows it.</summary>
/// <param name="RetentionDays">How long photos are kept without a subscription; null while subscribed.</param>
/// <param name="InvitesPerDollar">How many extra invitations each dollar buys once the first 50 are paid for.</param>
public sealed record PlanDto(
    string Kind, string Name, decimal Price, string Billing, decimal? YearlyPrice,
    long EventBytes, long? AccountBytes, int MaxBuckets, int MaxWindowDays, int? RetentionDays,
    bool IncludesFirstSend, int InvitesPerDollar,
    /// <summary>Whether each bucket's size can be set, sharing the account's space.</summary>
    bool Allocatable = false,
    long? StartingBucketBytes = null);

public sealed record SendingPriceDto(
    decimal Minimum, int IncludedInvites, decimal PerBlock, int BlockSize, int PremiumBlockSize);

public sealed record LapseDto(int ReminderDay, int OrganiserOnlyDay, int FinalNoticeDay, int DeleteDay);

public sealed record PlanCatalogDto(
    string Currency, IReadOnlyList<PlanDto> Plans, SendingPriceDto Sending, LapseDto Lapse);

/// <summary>What one event may do right now, and how long it is covered for.</summary>
/// <param name="CoveredUntil">When its cover ends; null while an active subscription covers it.</param>
/// <param name="OwnerUserId">The account whose subscription applies and whose storage limit counts it.</param>
public sealed record EventPlan(
    PlanKind Kind,
    long EventBytes,
    long? AccountBytes,
    int MaxBuckets,
    int MaxWindowDays,
    int InviteBlockSize,
    bool PassCoversFirstSend,
    DateTimeOffset? CoveredUntil,
    MediaPhase Phase,
    Guid? OwnerUserId,
    /// <summary>
    /// On Basic and Premium the account's space is shared out bucket by bucket: each bucket holds
    /// what it is given, and the gifts can't add up to more than the account has.
    /// </summary>
    bool Allocatable = false,
    long DefaultBucketBytes = 0);

/// <summary>The rules, with no database in them, so every branch can be tested directly.</summary>
public static class PlanRules
{
    public static bool IsActive(SubscriptionTier tier, DateTimeOffset? endsAt, DateTimeOffset now) =>
        tier != SubscriptionTier.None && (endsAt is null || endsAt > now);

    /// <param name="legacyBytes">Space held by buckets made before <see cref="PlanCatalog.IntroducedAt"/>.</param>
    /// <param name="legacyCoverUntil">The latest date anything from before the plans is covered to.</param>
    public static EventPlan Evaluate(
        DateTimeOffset now,
        DateTimeOffset eventDate,
        SubscriptionTier tier,
        DateTimeOffset? subscriptionEndsAt,
        DateTimeOffset? eventPassUntil,
        long legacyBytes,
        DateTimeOffset? legacyCoverUntil,
        DateTimeOffset? mediaDeletedAt,
        Guid? ownerUserId)
    {
        var subscribed = IsActive(tier, subscriptionEndsAt, now);
        var premium = subscribed && tier == SubscriptionTier.Premium;
        var basic = subscribed && tier == SubscriptionTier.Basic;
        var pass = eventPassUntil is { } until && until > now;

        var kind = premium ? PlanKind.Premium
            : pass ? PlanKind.EventPass
            : basic ? PlanKind.Basic
            : PlanKind.Free;
        var large = kind is PlanKind.Premium or PlanKind.EventPass;

        var eventBytes = kind switch
        {
            PlanKind.Free => PlanCatalog.FreeEventBytes,
            PlanKind.Basic => PlanCatalog.BasicEventBytes,
            _ => PlanCatalog.LargeEventBytes,
        };
        // Buckets made before the plans keep their space until six months after their event.
        if (legacyBytes > eventBytes && now < eventDate.AddMonths(PlanCatalog.EventPassMonths))
            eventBytes = legacyBytes;

        DateTimeOffset? coveredUntil = null;
        if (!subscribed)
        {
            var end = eventDate.AddDays(PlanCatalog.FreeCoverDays);
            foreach (var candidate in new[] { eventPassUntil, subscriptionEndsAt, legacyCoverUntil })
                if (candidate is { } c && c > end) end = c;
            coveredUntil = end;
        }

        return new EventPlan(
            kind,
            eventBytes,
            kind switch
            {
                PlanKind.Basic => PlanCatalog.BasicAccountBytes,
                PlanKind.Premium => PlanCatalog.PremiumAccountBytes,
                _ => null,
            },
            large ? MediaBucket.MaxPerCampaign : 1,
            large ? EventDayWindow.MaxWindowDays : 1,
            premium ? PricingCalculator.DesignerBlockSize : PricingCalculator.StandardBlockSize,
            pass,
            coveredUntil,
            PhaseOf(now, coveredUntil, mediaDeletedAt),
            ownerUserId,
            kind is PlanKind.Basic or PlanKind.Premium,
            kind switch
            {
                PlanKind.Basic => PlanCatalog.BasicBucketBytes,
                PlanKind.Premium => PlanCatalog.PremiumBucketBytes,
                _ => 0,
            });
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
