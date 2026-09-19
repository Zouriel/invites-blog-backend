namespace InvitesBlog.Application.Pricing;

/// <summary>A fully itemized price breakdown for sending, safe to show at checkout.</summary>
/// <param name="InviteCount">How many invitations the price covers.</param>
/// <param name="IncludedInvites">How many of them the event's pass already includes.</param>
/// <param name="ExtraInvites">The rest, which are paid for.</param>
/// <param name="ExtraBlocks">How many blocks of <paramref name="BlockSize"/> that takes.</param>
public sealed record PriceBreakdown(
    int InviteCount,
    int IncludedInvites,
    int ExtraInvites,
    int ExtraBlocks,
    int BlockSize,
    decimal PerBlock,
    decimal Total,
    string Currency = Plans.PlanCatalog.Currency);

/// <summary>
/// What invites.blog charges to SEND invitations. Sharing links yourself is always free. A Party pass
/// includes the first 100 and a Wedding pass the first 500; beyond that, and for an event without a
/// pass, it is <see cref="PricePerBlock"/> for every <see cref="BlockSize"/>. No minimum, and a top-up
/// only pays for what is not already covered. Pure and deterministic — the unit tests pin every branch.
/// </summary>
public static class PricingCalculator
{
    public const int BlockSize = 100;
    public const decimal PricePerBlock = 50m;

    /// <summary>The price of sending to <paramref name="inviteCount"/> guests, <paramref name="includedInvites"/> of them already paid for by the event's pass.</summary>
    public static PriceBreakdown CalculateInitial(int inviteCount, int includedInvites = 0, decimal perBlock = PricePerBlock)
    {
        if (inviteCount < 0) throw new ArgumentOutOfRangeException(nameof(inviteCount));
        if (includedInvites < 0) throw new ArgumentOutOfRangeException(nameof(includedInvites));

        var included = Math.Min(inviteCount, includedInvites);
        var extraInvites = inviteCount - included;
        var extraBlocks = (int)Math.Ceiling(extraInvites / (double)BlockSize);
        var total = extraBlocks * perBlock;

        return new PriceBreakdown(inviteCount, included, extraInvites, extraBlocks, BlockSize, perBlock, total);
    }

    /// <summary>
    /// Price for a top-up. Capacity already paid for, or included by the pass, is used first; only
    /// guests beyond it are charged, a block at a time.
    /// </summary>
    /// <param name="coveredCapacity">What is already covered: paid for, plus what the pass includes.</param>
    public static PriceBreakdown CalculateTopUp(int coveredCapacity, int currentGuestCount, int additionalGuests, decimal perBlock = PricePerBlock)
    {
        if (coveredCapacity < 0) throw new ArgumentOutOfRangeException(nameof(coveredCapacity));
        if (currentGuestCount < 0) throw new ArgumentOutOfRangeException(nameof(currentGuestCount));
        if (additionalGuests < 0) throw new ArgumentOutOfRangeException(nameof(additionalGuests));

        var shortfall = Math.Max(0, currentGuestCount + additionalGuests - coveredCapacity);
        var extraBlocks = (int)Math.Ceiling(shortfall / (double)BlockSize);
        var total = extraBlocks * perBlock;

        return new PriceBreakdown(additionalGuests, 0, shortfall, extraBlocks, BlockSize, perBlock, total);
    }

    /// <summary>New paid capacity after a top-up that bought <paramref name="blocks"/> blocks.</summary>
    public static int CapacityAfterTopUp(int currentPaidCapacity, int blocks) =>
        currentPaidCapacity + blocks * BlockSize;
}
