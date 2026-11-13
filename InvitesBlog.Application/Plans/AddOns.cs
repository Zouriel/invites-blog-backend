using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Plans;

/// <summary>One add-on as /api/plans offers it.</summary>
/// <param name="Price">Rufiyaa a month, from the price book.</param>
/// <param name="Usd">The store tier in dollars (the "+$5" wording); the apps show the store's own price.</param>
/// <param name="ForKinds">The plan kinds it can be added to.</param>
public sealed record AddOnDto(string Key, string Name, decimal Price, string Billing, decimal Usd, IReadOnlyList<string> ForKinds);

/// <summary>One add-on on an account.</summary>
/// <param name="Active">In force now.</param>
/// <param name="Renews">Part of the plan's next renewal (web: charged with it; stores: the subscription renews with it).</param>
/// <param name="EndsAt">When what was paid for it ends; null for no end, or when <paramref name="Included"/>.</param>
/// <param name="Included">Comes with the plan (Studio) or the role (admin): nothing to buy or turn off.</param>
public sealed record AccountAddOnDto(string Key, bool Active, bool Renews, DateTimeOffset? EndsAt, bool Included = false);

/// <summary>
/// Add-ons to a plan (owner, 2026-11-13): the AI designer is +77 a month on Premium; Studio has it included.
/// Kept as a list so a later one (vector generation) is another key, not another model. An add-on counts only
/// while the plan it belongs to is in force.
/// </summary>
public static class AddOns
{
    public const string DesignerAi = "designer-ai";

    /// <summary>The add-on's store tier, in dollars.</summary>
    public const decimal DesignerAiUsd = 5m;

    /// <summary>The least a prorated add-on costs on the website.</summary>
    public const decimal MinProrated = 5m;

    public static class Sources
    {
        public const string Bml = "bml";
        public const string GooglePlay = "google_play";
        public const string AppStore = "app_store";
        public const string Admin = "admin";
        /// <summary>Premium before 2026-11-13 had the AI designer: kept until its period ended.</summary>
        public const string CarriedOver = "carried-over";
    }

    public static IReadOnlyList<AddOnDto> Describe(Prices p) =>
        [new AddOnDto(DesignerAi, "AI designer", p.DesignerAiMonthly, "per month", DesignerAiUsd, ["Premium"])];

    public static AccountAddOn? Find(AppUser user, string key) => user.AddOns.FirstOrDefault(a => a.Key == key);

    public static bool PremiumActive(AppUser user, DateTimeOffset now) =>
        user.SubscriptionTier == SubscriptionTier.Premium && PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now);

    /// <summary>
    /// Studio or Photographer in force (<see cref="SubscriptionTiers.IsStudioClass"/>): the portfolio, Download all,
    /// the account's badge. Only the AI designer is Studio's alone (<see cref="VenueActive"/>).
    /// </summary>
    public static bool StudioActive(AppUser user, DateTimeOffset now) =>
        SubscriptionTiers.IsStudioClass(user.SubscriptionTier) && PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now);

    /// <summary>Studio itself (tier Venue) in force, which has the AI designer included; Photographer doesn't.</summary>
    public static bool VenueActive(AppUser user, DateTimeOffset now) =>
        user.SubscriptionTier == SubscriptionTier.Venue && PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now);

    /// <summary>Photographer in force: Studio without the AI designer (owner, 2026-11-13).</summary>
    public static bool PhotographerActive(AppUser user, DateTimeOffset now) =>
        user.SubscriptionTier == SubscriptionTier.Photographer && PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now);

    /// <summary>The add-on itself is in force: Premium is, and the add-on's paid time hasn't run out.</summary>
    public static bool DesignerAiActive(AppUser user, DateTimeOffset now) =>
        PremiumActive(user, now) && Find(user, DesignerAi) is { } a && (a.EndsAt is null || a.EndsAt > now);

    /// <summary>
    /// Whether the account may use the AI designer (the design assistant): Studio, or Premium with the add-on.
    /// Not Photographer (owner, 2026-11-13). Admins are let in by their permission where it is checked.
    /// </summary>
    public static bool HasDesignerAi(AppUser user, DateTimeOffset now) => VenueActive(user, now) || DesignerAiActive(user, now);

    /// <summary>The add-ons as the account and billing pages show them: one row per known add-on.</summary>
    public static IReadOnlyList<AccountAddOnDto> ForAccount(AppUser user, DateTimeOffset now, bool admin = false)
    {
        if (admin || VenueActive(user, now)) return [new AccountAddOnDto(DesignerAi, true, false, null, true)];
        var a = Find(user, DesignerAi);
        if (!DesignerAiActive(user, now)) return [new AccountAddOnDto(DesignerAi, false, false, null)];
        // A store renews (or not) what its sync recorded; a card plan only renews when its card renews Premium.
        var throughStore = a!.Source is Sources.GooglePlay or Sources.AppStore;
        var renews = a.Renews && (throughStore || user.AutoRenewKind is not null);
        return [new AccountAddOnDto(DesignerAi, true, renews, a.EndsAt)];
    }

    /// <summary>
    /// The add-on for the rest of a paid month: the days left x the monthly price / 30, rounded up to a whole
    /// rufiyaa, at least <see cref="MinProrated"/> and at most a whole month. No end date: a whole month.
    /// </summary>
    public static decimal ProratedDesignerAi(Prices p, DateTimeOffset? periodEndsAt, DateTimeOffset now)
    {
        var month = p.DesignerAiMonthly;
        if (periodEndsAt is not { } end) return month;
        var days = Math.Max(1m, Math.Ceiling((decimal)(end - now).TotalDays));
        var amount = Math.Ceiling(days * month / 30m);
        return Math.Min(month, Math.Max(Math.Min(MinProrated, month), amount));
    }

    /// <summary>Records an add-on (replacing the list, so the change is seen and saved).</summary>
    public static void Set(AppUser user, string key, DateTimeOffset? endsAt, bool renews, string source)
    {
        var list = user.AddOns.Where(a => a.Key != key).ToList();
        list.Add(new AccountAddOn { Key = key, EndsAt = endsAt, Renews = renews, Source = source });
        user.AddOns = list;
    }

    /// <summary>Changes whether an add-on renews. False when the account doesn't have it.</summary>
    public static bool SetRenews(AppUser user, string key, bool renews)
    {
        if (Find(user, key) is not { } a) return false;
        Set(user, key, a.EndsAt, renews, a.Source ?? Sources.Bml);
        return true;
    }

    /// <summary>The plan it belonged to stopped at <paramref name="end"/>: the add-on stops then too, and won't renew.</summary>
    public static void EndBy(AppUser user, string key, DateTimeOffset end)
    {
        if (Find(user, key) is not { } a) return;
        Set(user, key, a.EndsAt is { } e && e < end ? e : end, false, a.Source ?? Sources.Bml);
    }
}

/// <summary>
/// Downloading and sharing more than a few photos at once (owner, 2026-11-13): anyone may take up to
/// <see cref="Limit"/> at a time; "Download all" and more than that need the viewer's own Premium or Studio,
/// or an event with a Gold or Platinum pass in force. The server enforces it on every archive.
/// </summary>
public static class DownloadRules
{
    public const int Limit = 10;

    public const string ErrorCode = "download_limit";

    public const string Message = "Download more than 10 at once with Premium.";

    /// <summary>The account itself may take any number: Premium, Photographer or Studio in force.</summary>
    public static bool AccountAllows(AppUser? user, DateTimeOffset now) =>
        user is not null && (AddOns.PremiumActive(user, now) || AddOns.StudioActive(user, now));

    /// <summary>The event itself lets anyone who sees its photos take any number: a Gold or Platinum pass in force.</summary>
    public static bool EventAllows(Campaign campaign, DateTimeOffset now) =>
        EventPasses.Active(campaign, now) != EventPassKind.None;

    /// <summary>Whether this many named photos (none named: all of them) fits without the allowance.</summary>
    public static bool WithinLimit(IReadOnlyCollection<Guid>? ids) => ids is { Count: > 0 and <= Limit };

    /// <summary>The refusal every archive gives past the limit: 403, code <see cref="ErrorCode"/>.</summary>
    public static Exceptions.CodedRuleException Refusal() =>
        new(Message, ErrorCode, System.Net.HttpStatusCode.Forbidden);
}
