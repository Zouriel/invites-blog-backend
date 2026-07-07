namespace InvitesBlog.Application.Pricing;

/// <summary>A fully itemized price breakdown, safe to show at checkout.</summary>
public sealed record PriceBreakdown(
    int InviteCount,
    int IncludedInvites,
    int ExtraInvites,
    int ExtraBlocks,
    int BlockSize,
    decimal MinimumPrice,
    decimal ExtraCost,
    decimal Total,
    bool HasDesignerDiscount,
    string Currency = "USD");

/// <summary>
/// Implements the pricing model from spec §4.7.1–§4.7.2 and the top-up rules from §4.7.4:
///   - $5 minimum including 50 invites (minimum charged once per campaign)
///   - extra invites at $1 per 10 (or $1 per 20 with the community-designer discount, §6.5)
///   - top-ups reuse unused prepaid capacity first and never charge a second minimum
/// Pure and deterministic — the unit tests in InvitesBlog.Tests pin every branch.
/// </summary>
public static class PricingCalculator
{
    public const int IncludedInvites = 50;
    public const decimal MinimumPrice = 5m;
    public const int StandardBlockSize = 10;
    public const int DesignerBlockSize = 20;   // §6.5 — 50% off
    public const decimal PricePerBlock = 1m;

    public static int BlockSize(bool hasDesignerDiscount) =>
        hasDesignerDiscount ? DesignerBlockSize : StandardBlockSize;

    /// <summary>Price for the initial campaign payment (§4.7.2).</summary>
    public static PriceBreakdown CalculateInitial(int inviteCount, bool hasDesignerDiscount)
    {
        if (inviteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(inviteCount));

        var blockSize = BlockSize(hasDesignerDiscount);
        var extraInvites = Math.Max(0, inviteCount - IncludedInvites);
        var extraBlocks = (int)Math.Ceiling(extraInvites / (double)blockSize);
        var extraCost = extraBlocks * PricePerBlock;
        var total = MinimumPrice + extraCost;

        return new PriceBreakdown(
            InviteCount: inviteCount,
            IncludedInvites: IncludedInvites,
            ExtraInvites: extraInvites,
            ExtraBlocks: extraBlocks,
            BlockSize: blockSize,
            MinimumPrice: MinimumPrice,
            ExtraCost: extraCost,
            Total: total,
            HasDesignerDiscount: hasDesignerDiscount);
    }

    /// <summary>
    /// Price for a top-up (§4.7.4). Unused prepaid capacity is consumed first; only capacity
    /// beyond what the campaign already paid for is charged, at the per-block rate — no second
    /// $5 minimum. Returns a breakdown whose <see cref="PriceBreakdown.Total"/> is the top-up cost.
    /// </summary>
    public static PriceBreakdown CalculateTopUp(
        int currentPaidCapacity,
        int currentGuestCount,
        int additionalGuests,
        bool hasDesignerDiscount)
    {
        if (currentPaidCapacity < 0) throw new ArgumentOutOfRangeException(nameof(currentPaidCapacity));
        if (currentGuestCount < 0) throw new ArgumentOutOfRangeException(nameof(currentGuestCount));
        if (additionalGuests < 0) throw new ArgumentOutOfRangeException(nameof(additionalGuests));

        var blockSize = BlockSize(hasDesignerDiscount);
        var projectedGuests = currentGuestCount + additionalGuests;
        var capacityShortfall = Math.Max(0, projectedGuests - currentPaidCapacity);
        var extraBlocks = (int)Math.Ceiling(capacityShortfall / (double)blockSize);
        var total = extraBlocks * PricePerBlock;

        return new PriceBreakdown(
            InviteCount: additionalGuests,
            IncludedInvites: 0,
            ExtraInvites: capacityShortfall,
            ExtraBlocks: extraBlocks,
            BlockSize: blockSize,
            MinimumPrice: 0m,           // §4.7.4 / §23.3: minimum applies once per campaign
            ExtraCost: total,
            Total: total,
            HasDesignerDiscount: hasDesignerDiscount);
    }

    /// <summary>New paid capacity after a top-up that bought <paramref name="blocks"/> blocks.</summary>
    public static int CapacityAfterTopUp(int currentPaidCapacity, int blocks, bool hasDesignerDiscount) =>
        currentPaidCapacity + blocks * BlockSize(hasDesignerDiscount);
}
