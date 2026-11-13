using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.MediaBuckets;
using InvitesBlog.Application.Services.Payments;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Photographer (owner, 2026-11-13): everything Studio gives except the AI designer, MVR 540 a month on the website
/// and photographer_monthly in the stores. Sold, renewed and switched like Premium and Studio.
/// </summary>
public class PhotographerPlanTests
{
    private static readonly DateTimeOffset Now = new(2027, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private readonly AppUser _user = new() { Id = Guid.NewGuid(), Email = "lens@test.com", DisplayName = "Lens" };
    private readonly List<Payment> _payments = [];

    private BillingService Service()
    {
        var me = Substitute.For<ICurrentUser>();
        me.UserId.Returns(_user.Id);
        var users = Substitute.For<IRepository<AppUser>>();
        users.Query(Arg.Any<bool>()).Returns(_ => new[] { _user }.AsAsyncQueryable());
        var payments = Substitute.For<IPaymentRepository>();
        payments.Query(Arg.Any<bool>()).Returns(_ => _payments.AsAsyncQueryable());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:Enabled"] = "false",
        }).Build();
        return new BillingService(
            me, config, TestData.PriceBook(), TestData.FreePlans(), Substitute.For<ICampaignOwnershipService>(),
            Substitute.For<ICampaignRepository>(), users, payments, Substitute.For<IPassOfferService>(),
            Substitute.For<IPaymentProvider>(), TestData.Allowance(), Substitute.For<IMediaBucketService>(),
            Substitute.For<IRepository<AuditLog>>(), Substitute.For<IUnitOfWork>());
    }

    private Payment Paid(PaymentKind kind = PaymentKind.PhotographerMonthly)
    {
        var p = new Payment
        {
            Id = Guid.NewGuid(), UserId = _user.Id, Kind = kind, Quantity = 1,
            Amount = PlanCatalog.PhotographerMonthly, Currency = "MVR", Status = PaymentStatus.Paid, CreatedAt = DateTimeOffset.UtcNow,
        };
        _payments.Add(p);
        return p;
    }

    private void On(SubscriptionTier tier, int days = 20)
    {
        _user.SubscriptionTier = tier;
        _user.SubscriptionEndsAt = DateTimeOffset.UtcNow.AddDays(days);
    }

    // ----- The catalogue ------------------------------------------------------------------------------

    [Fact]
    public void The_catalogue_lists_photographer_between_premium_and_studio_at_540_with_studios_limits()
    {
        var plans = PlanCatalog.Describe().Plans;
        var kinds = plans.Select(p => p.Kind).ToList();
        Assert.Equal(["Free", "GoldPass", "PlatinumPass", "Premium", "Photographer", "Venue"], kinds);

        var photographer = plans.Single(p => p.Kind == "Photographer");
        var studio = plans.Single(p => p.Kind == "Venue");
        Assert.Equal("Photographer", photographer.Name);
        Assert.Equal(540m, photographer.Price);
        Assert.Equal("per month", photographer.Billing);
        Assert.Equal(studio.EventBytes, photographer.EventBytes);
        Assert.Equal(10 * PlanCatalog.Gb, photographer.EventBytes);
        Assert.Equal(studio.MaxBuckets, photographer.MaxBuckets);
        Assert.Equal(studio.Branded, photographer.Branded);
        Assert.Null(photographer.RetentionDays);
    }

    [Fact]
    public void Photographer_perks_are_studios_without_the_ai_designer_and_both_name_the_portfolio()
    {
        var photographer = PlanPerks.For(PlanKind.Photographer);
        var studio = PlanPerks.For(PlanKind.Venue);

        Assert.DoesNotContain(PlanPerks.DesignerAiIncluded, photographer);
        Assert.DoesNotContain(photographer, p => p.Contains("AI", StringComparison.Ordinal));
        Assert.Contains(PlanPerks.DesignerAiIncluded, studio);
        Assert.Equal(studio.Where(p => p != PlanPerks.DesignerAiIncluded), photographer);
        Assert.Contains(PlanPerks.Portfolio, photographer);
        Assert.Contains(PlanPerks.Portfolio, studio);
        // The AI add-on is Premium's only: never offered on the Photographer card.
        Assert.DoesNotContain("Photographer", AddOns.Describe(Prices.Defaults).Single().ForKinds);
    }

    [Fact]
    public void The_price_comes_from_the_price_book_and_an_old_saved_list_gets_the_default()
    {
        var catalog = PlanCatalog.Describe(Prices.Defaults with { PhotographerMonthly = 600m });
        Assert.Equal(600m, catalog.Plans.Single(p => p.Kind == "Photographer").Price);
        Assert.NotEmpty((Prices.Defaults with { PhotographerMonthly = 0 }).Problems());
        Assert.Equal(540m, Prices.Defaults.PhotographerMonthly);
    }

    // ----- Billing items ------------------------------------------------------------------------------

    [Fact]
    public void The_billing_item_is_photographer_monthly()
    {
        Assert.Equal(PaymentKind.PhotographerMonthly, BillingService.ParseItem("photographer-monthly"));
        Assert.Equal("photographer-monthly", BillingService.ItemName(PaymentKind.PhotographerMonthly));
        Assert.Equal("Photographer, a month", BillingService.Describe(PaymentKind.PhotographerMonthly, 1, null));
        Assert.True(BillingService.IsSubscription(PaymentKind.PhotographerMonthly));
        Assert.Equal(SubscriptionTier.Photographer, BillingService.TierOf(PaymentKind.PhotographerMonthly));
    }

    [Fact]
    public async Task A_paid_month_makes_the_account_a_photographer_for_a_month_and_another_follows()
    {
        await Service().FulfilAsync(Paid().Id);

        Assert.Equal(SubscriptionTier.Photographer, _user.SubscriptionTier);
        var ends = _user.SubscriptionEndsAt!.Value;
        Assert.InRange(ends, DateTimeOffset.UtcNow.AddMonths(1).AddMinutes(-1), DateTimeOffset.UtcNow.AddMonths(1).AddMinutes(1));

        await Service().FulfilAsync(Paid().Id);
        Assert.Equal(ends.AddMonths(1), _user.SubscriptionEndsAt);
    }

    [Fact]
    public async Task The_quote_says_it_renews_at_540()
    {
        var quote = await Service().QuoteAsync(new CheckoutRequest("photographer-monthly", null, 1, null));

        Assert.Equal(540m, quote.Amount);
        Assert.Equal(540m, quote.RenewsAmount);
        Assert.Equal("photographer-monthly", quote.RenewsItem);
    }

    [Theory]
    [InlineData(PaymentKind.PhotographerMonthly, SubscriptionTier.Premium, "billing_premium_account")]
    [InlineData(PaymentKind.PhotographerMonthly, SubscriptionTier.Venue, "billing_venue_account")]
    [InlineData(PaymentKind.PremiumMonthly, SubscriptionTier.Photographer, "billing_photographer_account")]
    [InlineData(PaymentKind.PremiumAiMonthly, SubscriptionTier.Photographer, "billing_photographer_account")]
    [InlineData(PaymentKind.VenueMonthly, SubscriptionTier.Photographer, "billing_photographer_account")]
    // The old pairs keep their answers.
    [InlineData(PaymentKind.VenueMonthly, SubscriptionTier.Premium, "billing_premium_account")]
    [InlineData(PaymentKind.PremiumMonthly, SubscriptionTier.Venue, "billing_venue_account")]
    public void Switching_between_plans_is_refused_at_checkout(PaymentKind buying, SubscriptionTier on, string code)
    {
        On(on);
        var ex = Assert.Throws<BusinessRuleException>(() => BillingService.EnsureAccountCanHave(buying, _user, DateTimeOffset.UtcNow));
        Assert.Equal(code, ex.ErrorCode);
    }

    [Theory]
    [InlineData(PaymentKind.PhotographerMonthly, SubscriptionTier.Photographer)]
    [InlineData(PaymentKind.PhotographerMonthly, SubscriptionTier.None)]
    [InlineData(PaymentKind.PremiumAiMonthly, SubscriptionTier.Premium)]
    [InlineData(PaymentKind.GoldPass, SubscriptionTier.Photographer)]
    public void Renewing_your_own_plan_or_starting_one_is_allowed(PaymentKind buying, SubscriptionTier on)
    {
        On(on);
        BillingService.EnsureAccountCanHave(buying, _user, DateTimeOffset.UtcNow);
    }

    [Fact]
    public void An_ended_plan_doesnt_stop_buying_another()
    {
        On(SubscriptionTier.Venue, days: -1);
        BillingService.EnsureAccountCanHave(PaymentKind.PhotographerMonthly, _user, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task The_ai_designer_add_on_cant_be_bought_on_photographer()
    {
        On(SubscriptionTier.Photographer);
        var ex = await Assert.ThrowsAsync<CodedRuleException>(() => Service().QuoteAsync(new CheckoutRequest("designer-ai-add", null, 1, null)));
        Assert.Equal("designer_ai_needs_premium", ex.ErrorCode);
    }

    // ----- What the account gets ---------------------------------------------------------------------

    [Fact]
    public void A_photographer_has_studio_perks_but_not_the_ai_designer()
    {
        On(SubscriptionTier.Photographer);
        var now = DateTimeOffset.UtcNow;

        Assert.True(AddOns.StudioActive(_user, now));
        Assert.True(AddOns.PhotographerActive(_user, now));
        Assert.False(AddOns.VenueActive(_user, now));
        Assert.False(AddOns.HasDesignerAi(_user, now));
        Assert.True(DownloadRules.AccountAllows(_user, now));
        var ai = Assert.Single(AddOns.ForAccount(_user, now));
        Assert.False(ai.Active);
        Assert.False(ai.Included);

        // Studio still has it included.
        On(SubscriptionTier.Venue);
        Assert.True(AddOns.HasDesignerAi(_user, now));
        Assert.True(Assert.Single(AddOns.ForAccount(_user, now)).Included);
    }

    [Fact]
    public void An_ended_photographer_has_nothing()
    {
        On(SubscriptionTier.Photographer, days: -1);
        var now = DateTimeOffset.UtcNow;
        Assert.False(AddOns.StudioActive(_user, now));
        Assert.False(DownloadRules.AccountAllows(_user, now));
    }

    // ----- Events a photographer organises -------------------------------------------------------------

    private static EventPlan Evaluate(bool active, DateTimeOffset? ended = null, EventPassKind pass = EventPassKind.None, DateTimeOffset? passUntil = null) =>
        PlanRules.Evaluate(Now, Now.AddDays(-20), pass, passUntil, null, (active, ended), null, null, Guid.Empty, photographer: true);

    [Fact]
    public void An_event_is_on_photographer_with_studios_limits_and_badge()
    {
        var plan = Evaluate(active: true);
        var studio = PlanRules.Evaluate(Now, Now.AddDays(-20), EventPassKind.None, null, null, (true, null), null, null, Guid.Empty);

        Assert.Equal(PlanKind.Photographer, plan.Kind);
        Assert.Equal(PlanKind.Venue, studio.Kind);
        Assert.Equal(studio with { Kind = PlanKind.Photographer }, plan);
        Assert.Equal(10 * PlanCatalog.Gb, plan.EventBytes);
        Assert.False(plan.Branded);
        Assert.True(PlanRules.IncludesFaceGrouping(plan));
        Assert.Null(plan.CoveredUntil);
        Assert.Equal(Guid.Empty, plan.VenueAccountId);
    }

    [Fact]
    public void A_pass_on_a_photographers_event_shows_the_pass_with_photographers_space_and_an_ended_one_is_like_studios()
    {
        var gold = Evaluate(active: true, pass: EventPassKind.Gold, passUntil: Now.AddMonths(6));
        Assert.Equal(PlanKind.GoldPass, gold.Kind);
        Assert.Equal(10 * PlanCatalog.Gb, gold.EventBytes);

        var ended = Evaluate(active: false, ended: Now.AddDays(-2));
        Assert.Equal(PlanKind.Free, ended.Kind);
        Assert.True(ended.VenueEnded);
        Assert.Null(ended.VenueAccountId);
    }

    // ----- Stores -------------------------------------------------------------------------------------

    [Fact]
    public void Photographer_monthly_is_sold_in_both_stores_as_a_subscription()
    {
        var play = GooglePlayCatalog.Find("photographer_monthly");
        Assert.NotNull(play);
        Assert.Equal(PaymentKind.PhotographerMonthly, play!.Kind);
        Assert.True(play.IsSubscription);

        Assert.Equal(PaymentKind.PhotographerMonthly, AppStoreCatalog.Find("photographer_monthly")!.Kind);
        Assert.Contains("photographer_monthly", AppStoreCatalog.Subscriptions);
        Assert.Equal("photographer_monthly", AppStoreCatalog.SubscriptionFor(SubscriptionTier.Photographer));
        Assert.Equal(["premium_monthly", "premium_ai_monthly", "photographer_monthly", "venue_monthly"], AppStoreCatalog.Subscriptions);
    }
}
