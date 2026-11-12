using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Everyone pays the same for a pass: the venue discount is gone (owner, 2026-11-12), since a venue's
/// plan now covers its events itself.
/// </summary>
public class PassOfferTests
{
    [Fact]
    public async Task Every_event_pays_the_price_book_s_prices()
    {
        var o = await new PassOfferService(TestData.PriceBook()).ForCampaignAsync(Guid.NewGuid());

        Assert.Equal(199m, o.GoldPass);
        Assert.Equal(499m, o.PlatinumPass);
        Assert.Equal(99m, o.GoldExtension);
        Assert.Equal(349m, o.PlatinumExtension);
        Assert.Equal(0, o.VenuePercent);
        Assert.Null(o.VenueName);
    }

    /// <summary>A price list saved while the discount existed still holds 50%; nothing applies it.</summary>
    [Fact]
    public async Task A_saved_venue_discount_is_ignored()
    {
        var book = TestData.PriceBook(Prices.Defaults with { VenueDiscountPercent = 50 });
        var o = await new PassOfferService(book).ForCampaignAsync(Guid.NewGuid());

        Assert.Equal(199m, o.GoldPass);
        Assert.Equal(349m, o.PlatinumExtension);
        Assert.Equal(0, o.VenuePercent);
        Assert.Equal(0, PlanCatalog.Describe(Prices.Defaults with { VenueDiscountPercent = 50 }).VenueDiscountPercent);
    }

    [Fact]
    public void Extending_adds_a_year_from_the_end_and_resets_the_reminders()
    {
        var now = DateTimeOffset.UtcNow;
        var c = TestData.Campaign();
        c.EventPass = EventPassKind.Platinum;
        c.EventPassUntil = now.AddDays(10);
        c.PassNoticeStage = 2;

        Assert.True(EventPasses.Extend(c, now));
        Assert.Equal(now.AddDays(10).AddMonths(12), c.EventPassUntil);
        Assert.Equal(0, c.PassNoticeStage);

        var none = TestData.Campaign();
        Assert.False(EventPasses.Extend(none, now));
    }
}
