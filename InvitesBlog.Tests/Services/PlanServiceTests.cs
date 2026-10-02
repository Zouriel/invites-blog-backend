using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>An event is on Premium when the person who organised it subscribes, not anyone else.</summary>
public class PlanServiceTests
{
    private static async Task<EventPlan> PlanFor(SubscriptionTier tier, DateTimeOffset? endsAt)
    {
        var organiser = new AppUser { Id = Guid.NewGuid(), Email = "host@x.mv", DisplayName = "Host", SubscriptionTier = tier, SubscriptionEndsAt = endsAt };
        var campaign = TestData.Campaign();
        campaign.CreatedByUserId = organiser.Id;
        campaign.EventStartAt = DateTimeOffset.UtcNow.AddDays(-400);

        var campaigns = Substitute.For<ICampaignRepository>();
        campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        var users = Substitute.For<IRepository<AppUser>>();
        users.GetByIdAsync(organiser.Id, Arg.Any<CancellationToken>()).Returns(organiser);
        var buckets = Substitute.For<IRepository<MediaBucket>>();
        buckets.Query(Arg.Any<bool>()).Returns(_ => Array.Empty<MediaBucket>().AsAsyncQueryable());

        return await new PlanService(campaigns, users, Substitute.For<IRepository<Venue>>(), buckets).ForCampaignAsync(campaign.Id);
    }

    [Fact]
    public async Task The_organiser_s_premium_covers_their_event()
    {
        var plan = await PlanFor(SubscriptionTier.Premium, null);
        Assert.Equal(PlanKind.Premium, plan.Kind);
        Assert.Equal(3 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Null(plan.CoveredUntil);
    }

    [Fact]
    public async Task A_venue_account_or_no_plan_is_not_premium()
    {
        Assert.Equal(PlanKind.Free, (await PlanFor(SubscriptionTier.Venue, null)).Kind);
        Assert.Equal(PlanKind.Free, (await PlanFor(SubscriptionTier.None, null)).Kind);
    }
}
