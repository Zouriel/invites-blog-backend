using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Legal;
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
/// Pricing 2026-11 (docs/PRICING-2026-11.md) on the website: Premium is 77 a month, the AI designer an add-on
/// of 77; Premium with it is 154; added to a running Premium it costs the rest of the paid month (rounded up, at
/// least 5); it renews with Premium until turned off, and then stays until the paid month ends.
/// </summary>
public class DesignerAiAddOnTests
{
    private static readonly DateTimeOffset Soon = DateTimeOffset.UtcNow.AddDays(15).AddMinutes(-30);

    private readonly AppUser _user = new() { Id = Guid.NewGuid(), Email = "host@test.com", DisplayName = "Host" };
    private readonly List<Payment> _payments = [];
    private readonly List<GooglePlayPurchase> _play = [];
    private readonly IPaymentProvider _provider = Substitute.For<IPaymentProvider>();
    private readonly IRecurringPaymentProvider _recurring = Substitute.For<IRecurringPaymentProvider>();

    private BillingService Service()
    {
        var me = Substitute.For<ICurrentUser>();
        me.UserId.Returns(_user.Id);
        var users = Substitute.For<IRepository<AppUser>>();
        users.Query(Arg.Any<bool>()).Returns(_ => new[] { _user }.AsAsyncQueryable());
        var payments = Substitute.For<IPaymentRepository>();
        payments.Query(Arg.Any<bool>()).Returns(_ => _payments.AsAsyncQueryable());
        payments.When(r => r.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())).Do(c => _payments.Add(c.Arg<Payment>()));
        var campaigns = Substitute.For<ICampaignRepository>();
        campaigns.Query(Arg.Any<bool>()).Returns(_ => Array.Empty<Campaign>().AsAsyncQueryable());
        var play = Substitute.For<IRepository<GooglePlayPurchase>>();
        play.Query(Arg.Any<bool>()).Returns(_ => _play.AsAsyncQueryable());
        _provider.Name.Returns("Bml");
        _provider.CreateCheckoutSessionAsync(Arg.Any<CreateCheckoutSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CheckoutSessionResult("sess-1", "https://pay.example/sess-1"));
        _recurring.CreateCustomerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns("cus_1");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:Enabled"] = "true",
            ["Urls:InviterBase"] = "https://invites.blog",
        }).Build();
        return new BillingService(
            me, config, TestData.PriceBook(), TestData.FreePlans(), Substitute.For<ICampaignOwnershipService>(),
            campaigns, users, payments, Substitute.For<IPassOfferService>(),
            _provider, TestData.Allowance(), Substitute.For<IMediaBucketService>(),
            Substitute.For<IRepository<AuditLog>>(), Substitute.For<IUnitOfWork>(),
            _recurring, Substitute.For<IPaymentService>(), play);
    }

    private void OnPremium(DateTimeOffset? ends = null, bool card = true)
    {
        _user.SubscriptionTier = SubscriptionTier.Premium;
        _user.SubscriptionEndsAt = ends ?? Soon;
        _user.AutoRenewKind = card ? PaymentKind.PremiumMonthly : null;
    }

    private void WithAi(DateTimeOffset? ends, bool renews, string source = "bml") =>
        _user.AddOns = [new AccountAddOn { Key = AddOns.DesignerAi, EndsAt = ends, Renews = renews, Source = source }];

    private static CheckoutRequest Item(string item) => new(item, null, 1, AcceptedTerms: true, TermsVersion: LegalTerms.Version);

    private async Task<Payment> PaidAsync(string item)
    {
        await Service().CheckoutAsync(Item(item));
        var payment = _payments.Last();
        payment.Status = PaymentStatus.Paid;
        await Service().FulfilAsync(payment.Id);
        return payment;
    }

    // ----- Prices -------------------------------------------------------------------------------

    [Fact]
    public async Task Premium_is_77_and_premium_with_the_ai_designer_is_154_renewing_at_that()
    {
        Assert.Equal(77m, Prices.Defaults.PremiumMonthly);
        Assert.Equal(77m, Prices.Defaults.DesignerAiMonthly);

        var premium = await Service().QuoteAsync(Item("premium-monthly"));
        Assert.Equal(77m, premium.Amount);
        Assert.Equal(77m, premium.RenewsAmount);
        Assert.Equal("premium-monthly", premium.RenewsItem);

        var withAi = await Service().QuoteAsync(Item("premium-ai-monthly"));
        Assert.Equal(154m, withAi.Amount);
        Assert.Equal(154m, withAi.RenewsAmount);
        Assert.Equal("premium-ai-monthly", withAi.Item);
        Assert.Equal("Premium with the AI designer, a month", withAi.Description);
        Assert.Equal("premium", withAi.InquireTopic);
    }

    [Theory]
    [InlineData(15.0, 39)]   // 15 days x 77/30 = 38.5, rounded up
    [InlineData(30.0, 77)]
    [InlineData(31.0, 77)]   // never more than a month
    [InlineData(0.04, 5)]    // an hour left: 1 day = 2.57, at least 5
    [InlineData(2.0, 6)]     // 2 days = 5.13 -> 6
    public void Added_mid_month_it_costs_the_days_left_rounded_up_at_least_5_at_most_a_month(double days, int expected)
    {
        var now = new DateTimeOffset(2026, 11, 13, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal(expected, AddOns.ProratedDesignerAi(Prices.Defaults, now.AddDays(days), now));
    }

    [Fact]
    public void A_premium_with_no_end_pays_a_whole_month_for_it()
    {
        Assert.Equal(77m, AddOns.ProratedDesignerAi(Prices.Defaults, null, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Adding_it_to_a_running_premium_quotes_the_rest_of_the_month_and_the_renewal_at_154()
    {
        OnPremium();

        var quote = await Service().QuoteAsync(Item("designer-ai-add"));

        Assert.Equal(39m, quote.Amount);
        Assert.Equal("designer-ai-add", quote.Item);
        Assert.Equal(154m, quote.RenewsAmount);
        Assert.Equal("premium-ai-monthly", quote.RenewsItem);
        Assert.Equal(Soon, quote.PeriodEndsAt);
    }

    [Fact]
    public async Task Paid_after_premium_renewed_it_covers_only_the_month_it_was_priced_for()
    {
        OnPremium();
        await Service().CheckoutAsync(Item("designer-ai-add"));
        var payment = _payments.Last();
        Assert.Equal(Soon, payment.PeriodEndsAt);

        // The payment waits; meanwhile the card renews Premium a month further.
        _user.SubscriptionEndsAt = Soon.AddMonths(1);
        payment.Status = PaymentStatus.Paid;
        await Service().FulfilAsync(payment.Id);

        var ai = Assert.Single(_user.AddOns, a => a.Key == AddOns.DesignerAi);
        Assert.Equal(Soon, ai.EndsAt);
    }

    // ----- Who may buy what ---------------------------------------------------------------------

    [Fact]
    public async Task The_add_on_needs_premium_and_is_refused_with_a_code_the_client_can_act_on()
    {
        var none = await Assert.ThrowsAsync<CodedRuleException>(() => Service().QuoteAsync(Item("designer-ai-add")));
        Assert.Equal("designer_ai_needs_premium", none.ErrorCode);
        Assert.Equal("designer_ai_needs_premium", Assert.Single(none.Errors!).Code);

        OnPremium();
        WithAi(Soon, true);
        Assert.Equal("designer_ai_already_on",
            (await Assert.ThrowsAsync<CodedRuleException>(() => Service().QuoteAsync(Item("designer-ai-add")))).ErrorCode);

        _user.AddOns = [];
        _user.SubscriptionTier = SubscriptionTier.Venue;
        Assert.Equal("designer_ai_included",
            (await Assert.ThrowsAsync<CodedRuleException>(() => Service().QuoteAsync(Item("designer-ai-add")))).ErrorCode);
    }

    [Fact]
    public async Task On_premium_already_premium_with_ai_is_refused_in_favour_of_the_prorated_add_on()
    {
        OnPremium();
        var e = await Assert.ThrowsAsync<CodedRuleException>(() => Service().QuoteAsync(Item("premium-ai-monthly")));
        Assert.Equal("designer_ai_use_add", e.ErrorCode);

        // With it already on, paying for the next month with it is fine.
        WithAi(Soon, true);
        Assert.Equal(154m, (await Service().QuoteAsync(Item("premium-ai-monthly"))).Amount);
    }

    [Fact]
    public async Task A_premium_billed_by_a_store_adds_it_in_the_app_not_on_the_website()
    {
        OnPremium(card: false);
        _play.Add(new GooglePlayPurchase
        {
            Id = Guid.NewGuid(), UserId = _user.Id, ProductId = "premium_monthly", PurchaseToken = "t",
            FulfilledAt = DateTimeOffset.UtcNow, ExpiresAt = Soon, AutoRenewing = true, CreatedAt = DateTimeOffset.UtcNow,
        });

        var e = await Assert.ThrowsAsync<CodedRuleException>(() => Service().QuoteAsync(Item("designer-ai-add")));
        Assert.Equal("designer_ai_store", e.ErrorCode);
    }

    // ----- What paying does ---------------------------------------------------------------------

    [Fact]
    public async Task Paying_premium_with_ai_gives_premium_and_the_add_on_to_the_same_end_renewing_by_card()
    {
        var payment = await PaidAsync("premium-ai-monthly");

        Assert.Equal(154m, payment.Amount);
        Assert.True(payment.AutoRenew);
        Assert.Equal(SubscriptionTier.Premium, _user.SubscriptionTier);
        // The plan renews as Premium; the add-on says it renews with it.
        Assert.Equal(PaymentKind.PremiumMonthly, _user.AutoRenewKind);
        var ai = AddOns.Find(_user, AddOns.DesignerAi)!;
        Assert.Equal(_user.SubscriptionEndsAt, ai.EndsAt);
        Assert.True(ai.Renews);
        Assert.Equal("bml", ai.Source);
        Assert.True(AddOns.HasDesignerAi(_user, DateTimeOffset.UtcNow));
        var row = Assert.Single(AddOns.ForAccount(_user, DateTimeOffset.UtcNow));
        Assert.Equal(new AccountAddOnDto(AddOns.DesignerAi, true, true, _user.SubscriptionEndsAt), row);
    }

    [Fact]
    public async Task Paying_premium_alone_gives_no_ai_designer()
    {
        await PaidAsync("premium-monthly");

        Assert.Equal(SubscriptionTier.Premium, _user.SubscriptionTier);
        Assert.False(AddOns.HasDesignerAi(_user, DateTimeOffset.UtcNow));
        Assert.Equal(new AccountAddOnDto(AddOns.DesignerAi, false, false, null), Assert.Single(AddOns.ForAccount(_user, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public async Task Adding_it_mid_month_turns_it_on_now_until_the_paid_month_ends_and_into_the_renewal()
    {
        OnPremium();

        var payment = await PaidAsync("designer-ai-add");

        Assert.Equal(39m, payment.Amount);
        Assert.False(payment.AutoRenew);
        Assert.Equal("AI designer, the rest of this month", payment.Description);
        Assert.Equal(Soon, _user.SubscriptionEndsAt);   // Premium itself isn't extended
        var ai = AddOns.Find(_user, AddOns.DesignerAi)!;
        Assert.Equal(Soon, ai.EndsAt);
        Assert.True(ai.Renews);
        Assert.True(AddOns.HasDesignerAi(_user, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Added_to_a_premium_that_does_not_renew_it_lasts_the_month_and_does_not_renew()
    {
        OnPremium(card: false);

        await PaidAsync("designer-ai-add");

        Assert.False(AddOns.Find(_user, AddOns.DesignerAi)!.Renews);
        Assert.False(Assert.Single(AddOns.ForAccount(_user, DateTimeOffset.UtcNow)).Renews);
    }

    // ----- Turning it off and on ----------------------------------------------------------------

    [Fact]
    public async Task Turning_it_off_keeps_it_until_the_month_ends_and_on_again_renews_it_without_a_charge()
    {
        OnPremium();
        WithAi(Soon, true);

        var off = await Service().SetDesignerAiRenewalAsync(false);
        var row = Assert.Single(off.AddOns!);
        Assert.True(row.Active);
        Assert.False(row.Renews);
        Assert.Equal(Soon, row.EndsAt);
        Assert.True(AddOns.HasDesignerAi(_user, DateTimeOffset.UtcNow));

        var on = await Service().SetDesignerAiRenewalAsync(true);
        Assert.True(Assert.Single(on.AddOns!).Renews);
        Assert.Empty(_payments);
    }

    [Fact]
    public async Task Turning_on_an_add_on_that_has_run_out_is_refused_and_says_to_add_it()
    {
        OnPremium();
        WithAi(DateTimeOffset.UtcNow.AddMinutes(-1), false);

        var e = await Assert.ThrowsAsync<CodedRuleException>(() => Service().SetDesignerAiRenewalAsync(true));
        Assert.Equal("designer_ai_not_on", e.ErrorCode);
    }

    [Fact]
    public async Task A_carried_over_add_on_can_be_kept_by_turning_it_on()
    {
        OnPremium();
        WithAi(Soon, false, source: AddOns.Sources.CarriedOver);

        var account = await Service().GetAsync();
        Assert.Equal(new AccountAddOnDto(AddOns.DesignerAi, true, false, Soon), Assert.Single(account.Account.AddOns!));
        Assert.True(account.Account.CanDownloadAll);
        Assert.Equal(77m, account.Prices.DesignerAiMonthly);
        Assert.Equal(77m, account.Prices.PremiumMonthly);

        Assert.True(Assert.Single((await Service().SetDesignerAiRenewalAsync(true)).AddOns!).Renews);
    }

    // ----- Who has it ---------------------------------------------------------------------------

    [Fact]
    public void Studio_and_admins_have_it_included_and_a_lapsed_premium_has_it_no_more()
    {
        var now = DateTimeOffset.UtcNow;
        _user.SubscriptionTier = SubscriptionTier.Venue;
        _user.SubscriptionEndsAt = now.AddDays(3);
        Assert.True(AddOns.HasDesignerAi(_user, now));
        Assert.Equal(new AccountAddOnDto(AddOns.DesignerAi, true, false, null, true), Assert.Single(AddOns.ForAccount(_user, now)));

        _user.SubscriptionTier = SubscriptionTier.None;
        Assert.True(Assert.Single(AddOns.ForAccount(_user, now, admin: true)).Included);

        // An add-on paid past the end of a Premium that has ended counts for nothing.
        OnPremium(now.AddMinutes(-1));
        WithAi(now.AddDays(5), true);
        Assert.False(AddOns.HasDesignerAi(_user, now));
        Assert.False(Assert.Single(AddOns.ForAccount(_user, now)).Active);
    }
}
