using InvitesBlog.Application.Pricing;
using Xunit;

namespace InvitesBlog.Tests;

public class PricingCalculatorTests
{
    [Theory]
    // §4.7.1: $5 minimum includes 50 invites.
    [InlineData(0, false, 5)]
    [InlineData(1, false, 5)]
    [InlineData(50, false, 5)]
    // Extra invites at $1 per 10 (blocks rounded up).
    [InlineData(51, false, 6)]    // 1 extra -> 1 block
    [InlineData(60, false, 6)]    // 10 extra -> 1 block
    [InlineData(61, false, 7)]    // 11 extra -> 2 blocks
    [InlineData(150, false, 15)]  // 100 extra -> 10 blocks
    public void Initial_standard_rate(int invites, bool discount, decimal expected)
    {
        var result = PricingCalculator.CalculateInitial(invites, discount);
        Assert.Equal(expected, result.Total);
    }

    [Theory]
    // §6.5: designer discount => $1 per 20 invites.
    [InlineData(50, true, 5)]
    [InlineData(70, true, 6)]     // 20 extra -> 1 block
    [InlineData(71, true, 7)]     // 21 extra -> 2 blocks
    [InlineData(150, true, 10)]   // 100 extra -> 5 blocks
    public void Initial_designer_rate(int invites, bool discount, decimal expected)
    {
        var result = PricingCalculator.CalculateInitial(invites, discount);
        Assert.Equal(expected, result.Total);
    }

    [Fact]
    public void Designer_discount_is_half_price_on_extras()
    {
        var standard = PricingCalculator.CalculateInitial(250, false); // 200 extra -> 20 blocks -> $25
        var designer = PricingCalculator.CalculateInitial(250, true);  // 200 extra -> 10 blocks -> $15
        Assert.Equal(25m, standard.Total);
        Assert.Equal(15m, designer.Total);
    }

    [Fact]
    public void TopUp_never_charges_a_second_minimum()
    {
        // Campaign paid for 50, has 50 guests, adds 5 more -> 1 block -> $1, no $5 minimum. (§4.7.4)
        var topUp = PricingCalculator.CalculateTopUp(
            currentPaidCapacity: 50, currentGuestCount: 50, additionalGuests: 5, hasDesignerDiscount: false);
        Assert.Equal(1m, topUp.Total);
        Assert.Equal(0m, topUp.MinimumPrice);
    }

    [Fact]
    public void TopUp_consumes_unused_capacity_first()
    {
        // Paid for 60, only 40 guests, adds 15 -> new total 55 <= 60 capacity -> free.
        var topUp = PricingCalculator.CalculateTopUp(
            currentPaidCapacity: 60, currentGuestCount: 40, additionalGuests: 15, hasDesignerDiscount: false);
        Assert.Equal(0m, topUp.Total);
        Assert.Equal(0, topUp.ExtraBlocks);
    }

    [Fact]
    public void TopUp_charges_only_capacity_beyond_prepaid()
    {
        // Paid 50, 50 guests, add 25 -> shortfall 25 -> 3 blocks (ceil 25/10) -> $3.
        var topUp = PricingCalculator.CalculateTopUp(50, 50, 25, false);
        Assert.Equal(3m, topUp.Total);
    }

    [Fact]
    public void CapacityAfterTopUp_grows_by_block_size()
    {
        Assert.Equal(80, PricingCalculator.CapacityAfterTopUp(50, 3, false)); // 50 + 3*10
        Assert.Equal(90, PricingCalculator.CapacityAfterTopUp(50, 2, true));  // 50 + 2*20
    }

    [Fact]
    public void Negative_invite_count_throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.CalculateInitial(-1, false));
    }
}
