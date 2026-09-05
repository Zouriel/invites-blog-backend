using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.MediaBuckets;

/// <summary>
/// What a media bucket costs and how big it is, as configuration rather than as constants.
///
/// <para><b>Why config.</b> A price is the one thing about this product certain to change, and
/// changing it should not be a deploy. Bound from <c>MediaBuckets</c> in appsettings; the values in
/// <see cref="Defaults"/> are what ships, so a missing section is a working product rather than a
/// free one.</para>
/// </summary>
public sealed class MediaBucketOptions
{
    public const string Section = "MediaBuckets";

    /// <summary>
    /// What a bucket holds before anyone has bought anything — including every event photo box that
    /// existed before buckets did.
    ///
    /// <para>Deliberately generous enough to be useful and small enough to be a reason to buy: a
    /// couple of gigabytes is a few hundred phone photographs, which covers a small party outright
    /// and runs out somewhere in the middle of a wedding.</para>
    /// </summary>
    public int FreeGb { get; set; } = 2;

    /// <summary>How long a paid term runs before it needs renewing.</summary>
    public int TermMonths { get; set; } = 6;

    public string Currency { get; set; } = "USD";

    /// <summary>The paid tiers, keyed by <see cref="MediaBucketTier"/> name.</summary>
    public Dictionary<string, decimal> Prices { get; set; } = new();

    /// <summary>
    /// Ships as USD 9/15/19/29 per six months. Bigger tiers cost less per gigabyte on purpose —
    /// the storage does, and the alternative is a price list where the honest advice is always to
    /// buy the smallest one twice.
    /// </summary>
    public static readonly IReadOnlyDictionary<MediaBucketTier, decimal> Defaults =
        new Dictionary<MediaBucketTier, decimal>
        {
            [MediaBucketTier.Gb10] = 9m,
            [MediaBucketTier.Gb20] = 15m,
            [MediaBucketTier.Gb30] = 19m,
            [MediaBucketTier.Gb50] = 29m,
        };
}

/// <summary>One tier as it is offered: how big, how much, for how long.</summary>
/// <param name="Tier">The stored value. What is frozen onto a bucket, not the label.</param>
/// <param name="Gb">Size in gigabytes, for the label.</param>
/// <param name="CapacityBytes">The same size in the units the quota is actually checked in.</param>
/// <param name="Price">Zero for the free tier, which is offered but never bought.</param>
public sealed record MediaBucketPlan(
    MediaBucketTier Tier,
    int Gb,
    long CapacityBytes,
    decimal Price,
    string Currency,
    int TermMonths)
{
    public bool IsFree => Tier == MediaBucketTier.Free;
}

/// <summary>Turns <see cref="MediaBucketOptions"/> into the plans the rest of the app asks about.</summary>
public static class MediaBucketPlans
{
    /// <summary>
    /// Gigabytes, in bytes. 1024-based rather than 1000-based because the number this is compared
    /// against is a file size measured the same way — a "10 GB" bucket that filled up at 9.31 GB by
    /// the only measure the customer can see would read as us shortchanging them.
    /// </summary>
    public const long BytesPerGb = 1024L * 1024 * 1024;

    private static readonly IReadOnlyDictionary<MediaBucketTier, int> Sizes =
        new Dictionary<MediaBucketTier, int>
        {
            [MediaBucketTier.Gb10] = 10,
            [MediaBucketTier.Gb20] = 20,
            [MediaBucketTier.Gb30] = 30,
            [MediaBucketTier.Gb50] = 50,
        };

    /// <summary>Every tier that can be bought, smallest first. The free tier is not among them.</summary>
    public static IReadOnlyList<MediaBucketPlan> Purchasable(MediaBucketOptions options) =>
        Sizes.OrderBy(kv => kv.Value)
            .Select(kv => new MediaBucketPlan(
                kv.Key,
                kv.Value,
                kv.Value * BytesPerGb,
                PriceOf(kv.Key, options),
                options.Currency,
                options.TermMonths))
            .ToList();

    /// <summary>The free tier, as a plan, so callers do not special-case it into existence.</summary>
    public static MediaBucketPlan Free(MediaBucketOptions options) => new(
        MediaBucketTier.Free, options.FreeGb, options.FreeGb * BytesPerGb, 0m, options.Currency, 0);

    public static MediaBucketPlan? For(MediaBucketTier tier, MediaBucketOptions options) =>
        tier == MediaBucketTier.Free
            ? Free(options)
            : Purchasable(options).FirstOrDefault(p => p.Tier == tier);

    /// <summary>
    /// Configuration wins, then the shipped default. A tier priced at nothing by a typo would
    /// otherwise be given away silently, so an unconfigured tier falls back rather than to zero.
    /// </summary>
    private static decimal PriceOf(MediaBucketTier tier, MediaBucketOptions options) =>
        options.Prices.TryGetValue(tier.ToString(), out var configured) && configured > 0
            ? configured
            : MediaBucketOptions.Defaults.GetValueOrDefault(tier);
}
