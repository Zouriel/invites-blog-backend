using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The one discount is a venue's: an event a venue runs has its passes and extensions at half price.
/// A design made for someone is full price (the Studio discount went with Studio).
/// </summary>
public class PassOfferTests
{
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();

    private readonly AppUser _host = new() { Id = Guid.NewGuid(), Email = "client@x.mv", DisplayName = "Client" };

    [Fact]
    public async Task A_design_made_for_the_client_pays_full_price()
    {
        var campaign = TestData.Campaign();
        campaign.CreatedByUserId = _host.Id;
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        var users = Substitute.For<IRepository<AppUser>>();
        users.Query(Arg.Any<bool>()).Returns(_ => new[] { _host }.AsAsyncQueryable());

        var o = await new PassOfferService(_campaigns, users, TestData.PriceBook()).ForCampaignAsync(campaign.Id);

        Assert.Equal(199m, o.GoldPass);
        Assert.Equal(499m, o.PlatinumPass);
        Assert.Equal(99m, o.GoldExtension);
        Assert.Equal(349m, o.PlatinumExtension);
        Assert.Equal(0, o.VenuePercent);
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

    // ----- venues -----

    private async Task<PassOfferDto> AtVenue(bool venueActive)
    {
        var owner = new AppUser
        {
            Id = Guid.NewGuid(), Email = "resort@x.mv", DisplayName = "Resort",
            SubscriptionTier = venueActive ? SubscriptionTier.Venue : SubscriptionTier.None,
        };
        var venue = new Venue { Id = Guid.NewGuid(), OwnerUserId = owner.Id, Name = "Sunset Resort" };
        var campaign = TestData.Campaign();
        campaign.CreatedByUserId = _host.Id;
        campaign.VenueId = venue.Id;
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        var users = Substitute.For<IRepository<AppUser>>();
        users.Query(Arg.Any<bool>()).Returns(_ => new[] { _host, owner }.AsAsyncQueryable());
        var venues = Substitute.For<IRepository<Venue>>();
        venues.Query(Arg.Any<bool>()).Returns(_ => new[] { venue }.AsAsyncQueryable());
        return await new PassOfferService(_campaigns, users, TestData.PriceBook(), venues).ForCampaignAsync(campaign.Id);
    }

    /// <summary>A venue buys and renews its events' passes at half price, and charges its clients itself.</summary>
    [Fact]
    public async Task A_venue_s_event_has_passes_and_extensions_at_half_price()
    {
        var o = await AtVenue(venueActive: true);

        Assert.Equal(100m, o.GoldPass);        // half of 199, rounded
        Assert.Equal(250m, o.PlatinumPass);      // half of 499 (249.50), rounded up
        Assert.Equal(50m, o.GoldExtension);    // half of 99
        Assert.Equal(175m, o.PlatinumExtension); // half of 349
        Assert.Equal(199m, o.FullGoldPass);
        Assert.Equal(50, o.VenuePercent);
        Assert.Equal("Sunset Resort", o.VenueName);
    }

    [Fact]
    public async Task A_venue_whose_account_was_taken_away_pays_full_price()
    {
        var o = await AtVenue(venueActive: false);

        Assert.Equal(199m, o.GoldPass);
        Assert.Equal(99m, o.GoldExtension);
        Assert.Equal(0, o.VenuePercent);
    }
}
