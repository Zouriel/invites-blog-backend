using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// An event is on Premium, or on Studio (tier Venue), when the person who organised it subscribes, not anyone
/// else (owner, 2026-11-12: Studio is an account plan, like Premium; there are no venue pages or codes).
/// </summary>
public class PlanServiceTests
{
    private sealed record Setup(PlanService Service, Campaign Campaign, AppUser Organiser);

    private static Setup Make(SubscriptionTier tier, DateTimeOffset? endsAt, Action<Campaign>? edit = null,
        string name = "Coco Palm", string? avatar = "https://cdn.example/a.webp")
    {
        var organiser = new AppUser
        {
            Id = Guid.NewGuid(), Email = "host@x.mv", DisplayName = name, AvatarUrl = avatar,
            SubscriptionTier = tier, SubscriptionEndsAt = endsAt,
        };
        var campaign = TestData.Campaign();
        campaign.CreatedByUserId = organiser.Id;
        campaign.EventStartAt = DateTimeOffset.UtcNow.AddDays(-400);
        edit?.Invoke(campaign);

        var campaigns = Substitute.For<ICampaignRepository>();
        campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        var users = Substitute.For<IRepository<AppUser>>();
        users.GetByIdAsync(organiser.Id, Arg.Any<CancellationToken>()).Returns(organiser);
        users.Query(Arg.Any<bool>()).Returns(_ => new[] { organiser }.AsAsyncQueryable());
        var buckets = Substitute.For<IRepository<MediaBucket>>();
        buckets.Query(Arg.Any<bool>()).Returns(_ => Array.Empty<MediaBucket>().AsAsyncQueryable());

        return new Setup(new PlanService(campaigns, users, buckets), campaign, organiser);
    }

    private static Task<EventPlan> PlanFor(SubscriptionTier tier, DateTimeOffset? endsAt, Action<Campaign>? edit = null)
    {
        var s = Make(tier, endsAt, edit);
        return s.Service.ForCampaignAsync(s.Campaign.Id);
    }

    [Fact]
    public async Task The_organiser_s_premium_covers_their_event()
    {
        var plan = await PlanFor(SubscriptionTier.Premium, null);
        Assert.Equal(PlanKind.Premium, plan.Kind);
        Assert.Equal(2 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Null(plan.CoveredUntil);
        Assert.Null(plan.VenueAccountId);
    }

    [Fact]
    public async Task No_plan_is_free()
    {
        var plan = await PlanFor(SubscriptionTier.None, null);
        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Null(plan.VenueAccountId);
        Assert.False(plan.VenueEnded);
    }

    /// <summary>The organiser's Studio plan covers every event they organise: 10 GB, no mark, faces, kept.</summary>
    [Fact]
    public async Task The_organiser_s_studio_plan_covers_their_event_on_its_own()
    {
        var s = Make(SubscriptionTier.Venue, DateTimeOffset.UtcNow.AddDays(20));
        var plan = await s.Service.ForCampaignAsync(s.Campaign.Id);

        Assert.Equal(PlanKind.Venue, plan.Kind);
        Assert.Equal(PlanCatalog.VenueEventBytes, plan.EventBytes);
        Assert.Equal(1, plan.MaxBuckets);
        Assert.Equal(PlanCatalog.FreeWindowDays, plan.MaxWindowDays);
        Assert.False(plan.Branded);
        Assert.True(PlanRules.IncludesFaceGrouping(plan));
        Assert.Null(plan.CoveredUntil);
        Assert.Equal(MediaPhase.Active, plan.Phase);
        Assert.Equal(s.Organiser.Id, plan.VenueAccountId);
        Assert.False(plan.VenueEnded);
    }

    [Fact]
    public async Task With_a_pass_as_well_each_limit_is_the_larger()
    {
        var plan = await PlanFor(SubscriptionTier.Venue, null, c =>
        {
            c.EventPass = EventPassKind.Gold;
            c.EventPassUntil = DateTimeOffset.UtcNow.AddMonths(3);
        });

        Assert.Equal(PlanKind.GoldPass, plan.Kind);
        Assert.Equal(PlanCatalog.VenueEventBytes, plan.EventBytes);   // 10 GB beats Gold's 5
        Assert.Equal(PlanCatalog.GoldBuckets, plan.MaxBuckets);
        Assert.Equal(PlanCatalog.GoldWindowDays, plan.MaxWindowDays);
        Assert.Equal(PlanCatalog.GoldIncludedInvites, plan.IncludedInvites);
        Assert.False(plan.Branded);
        Assert.Null(plan.CoveredUntil);
        Assert.NotNull(plan.VenueAccountId);
    }

    /// <summary>When Studio ends: like Premium, uploads close, then the warnings and the cut to Free.</summary>
    [Fact]
    public async Task When_the_organiser_s_studio_plan_ends_the_event_goes_back_to_free_like_premium()
    {
        var ended = DateTimeOffset.UtcNow.AddDays(-2);
        var plan = await PlanFor(SubscriptionTier.Venue, ended);

        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Equal(PlanCatalog.FreeEventBytes, plan.EventBytes);
        Assert.True(plan.Branded);
        Assert.Equal(ended, plan.CoveredUntil);
        Assert.Equal(MediaPhase.UploadsClosed, plan.Phase);
        Assert.Null(plan.VenueAccountId);
        Assert.True(plan.VenueEnded);
    }

    /// <summary>An organiser switched to Premium (the admin switch) is on Premium, not Studio.</summary>
    [Fact]
    public async Task An_organiser_switched_to_premium_is_on_premium()
    {
        var plan = await PlanFor(SubscriptionTier.Premium, null);
        Assert.Equal(PlanKind.Premium, plan.Kind);
        Assert.Null(plan.VenueAccountId);
        Assert.False(plan.VenueEnded);
    }

    /// <summary>The old venue link on an event counts for nothing now: only the organiser's plan does.</summary>
    [Fact]
    public async Task A_leftover_venue_link_gives_a_free_organiser_s_event_nothing()
    {
        var plan = await PlanFor(SubscriptionTier.None, null, c => c.VenueId = Guid.NewGuid());
        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Null(plan.VenueAccountId);
    }

    [Fact]
    public async Task Many_events_at_once_give_the_same_answer()
    {
        var s = Make(SubscriptionTier.Venue, null);
        var plans = await s.Service.ForCampaignsAsync([s.Campaign]);
        Assert.Equal(PlanKind.Venue, plans[s.Campaign.Id].Kind);
        Assert.Equal(s.Organiser.Id, plans[s.Campaign.Id].VenueAccountId);
    }

    // ---------- the badge: the organiser's own name and picture ----------

    [Fact]
    public async Task The_badge_is_the_organiser_s_name_and_picture_while_on_studio()
    {
        var s = Make(SubscriptionTier.Venue, DateTimeOffset.UtcNow.AddDays(5), name: "Coco Palm", avatar: "https://cdn.example/me.webp");
        var faces = await s.Service.VenueFacesAsync([s.Campaign]);

        Assert.Equal(new VenueFace("Coco Palm", "https://cdn.example/me.webp"), faces[s.Campaign.Id]);
    }

    [Fact]
    public async Task There_is_no_badge_on_premium_on_no_plan_or_once_studio_has_ended()
    {
        foreach (var (tier, ends) in new (SubscriptionTier, DateTimeOffset?)[]
                 {
                     (SubscriptionTier.Premium, null),
                     (SubscriptionTier.None, null),
                     (SubscriptionTier.Venue, DateTimeOffset.UtcNow.AddDays(-1)),
                 })
        {
            var s = Make(tier, ends);
            Assert.Empty(await s.Service.VenueFacesAsync([s.Campaign]));
        }
    }
}
