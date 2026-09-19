using InvitesBlog.Application.Pricing;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>What sending costs: MVR 50 for every 100 invitations, after whatever the pass includes.</summary>
public class PricingCalculatorTests
{
    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(1, 0, 50)]
    [InlineData(100, 0, 50)]
    [InlineData(101, 0, 100)]
    [InlineData(350, 0, 200)]
    public void Without_a_pass_every_100_is_mvr_50_with_no_minimum(int invites, int included, decimal expected)
    {
        var result = PricingCalculator.CalculateInitial(invites, included);
        Assert.Equal(expected, result.Total);
        Assert.Equal("MVR", result.Currency);
    }

    [Theory]
    // Party pass: the first 100 are included.
    [InlineData(80, 100, 0)]
    [InlineData(100, 100, 0)]
    [InlineData(101, 100, 50)]
    [InlineData(250, 100, 100)]
    // Wedding pass: the first 500.
    [InlineData(500, 500, 0)]
    [InlineData(620, 500, 100)]
    public void A_pass_includes_its_invitations_first(int invites, int included, decimal expected)
    {
        var result = PricingCalculator.CalculateInitial(invites, included);
        Assert.Equal(expected, result.Total);
        Assert.Equal(Math.Min(invites, included), result.IncludedInvites);
        Assert.Equal(invites - result.IncludedInvites, result.ExtraInvites);
    }

    [Fact]
    public void A_top_up_only_pays_for_guests_beyond_what_is_covered()
    {
        Assert.Equal(0m, PricingCalculator.CalculateTopUp(200, 150, 50).Total);
        var topUp = PricingCalculator.CalculateTopUp(200, 190, 20);
        Assert.Equal(1, topUp.ExtraBlocks);
        Assert.Equal(50m, topUp.Total);
    }

    [Fact]
    public void Capacity_grows_by_a_block_at_a_time()
    {
        Assert.Equal(300, PricingCalculator.CapacityAfterTopUp(100, 2));
    }

    [Fact]
    public void Negative_counts_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.CalculateInitial(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => PricingCalculator.CalculateTopUp(0, -1, 0));
    }
}
