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
/// Bank of Maldives' card rules: the total, the currency and the terms are shown before paying, and
/// the buyer affirmatively accepts them. The quote is what the review step shows; checkout refuses to
/// start a payment without the acceptance, and records it on the payment when it has it.
/// </summary>
public class CheckoutTermsTests
{
    private readonly AppUser _user = new() { Id = Guid.NewGuid(), Email = "host@test.com" };
    private readonly List<Payment> _payments = [];
    private readonly IPaymentProvider _provider = Substitute.For<IPaymentProvider>();
    private readonly IRecurringPaymentProvider _recurring = Substitute.For<IRecurringPaymentProvider>();
    private readonly IPaymentService _paymentService = Substitute.For<IPaymentService>();

    private BillingService Service(bool paymentsOn)
    {
        var me = Substitute.For<ICurrentUser>();
        me.UserId.Returns(_user.Id);
        var users = Substitute.For<IRepository<AppUser>>();
        users.Query(Arg.Any<bool>()).Returns(_ => new[] { _user }.AsAsyncQueryable());
        var payments = Substitute.For<IPaymentRepository>();
        payments.Query(Arg.Any<bool>()).Returns(_ => _payments.AsAsyncQueryable());
        payments.When(r => r.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())).Do(c => _payments.Add(c.Arg<Payment>()));
        _provider.Name.Returns("Fake");
        _provider.CreateCheckoutSessionAsync(Arg.Any<CreateCheckoutSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CheckoutSessionResult("sess-1", "https://pay.example/sess-1"));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:Enabled"] = paymentsOn ? "true" : "false",
            ["Urls:InviterBase"] = "https://invites.blog",
        }).Build();
        return new BillingService(
            me, config, TestData.PriceBook(), TestData.FreePlans(), Substitute.For<ICampaignOwnershipService>(),
            Substitute.For<ICampaignRepository>(), users, payments, Substitute.For<IPassOfferService>(),
            _provider, TestData.Allowance(), Substitute.For<IMediaBucketService>(),
            Substitute.For<IRepository<AuditLog>>(), Substitute.For<IUnitOfWork>(),
            _recurring, _paymentService);
    }

    [Fact]
    public async Task The_quote_shows_the_total_in_rufiyaa_and_the_terms_version_without_starting_a_payment()
    {
        var quote = await Service(paymentsOn: true).QuoteAsync(new CheckoutRequest("premium-monthly", null, 1));

        Assert.True(quote.Available);
        Assert.Equal("premium-monthly", quote.Item);
        Assert.Equal("Premium pass, a month", quote.Description);
        Assert.Equal(Prices.Defaults.PremiumMonthly, quote.Amount);
        Assert.Equal("MVR", quote.Currency);
        Assert.Equal(LegalTerms.Version, quote.TermsVersion);
        Assert.Empty(_payments);
        await _provider.DidNotReceiveWithAnyArgs().CreateCheckoutSessionAsync(default!, default);
    }

    [Fact]
    public async Task While_payments_are_off_the_quote_says_so_and_where_to_ask()
    {
        var quote = await Service(paymentsOn: false).QuoteAsync(new CheckoutRequest("premium-monthly", null, 1));

        Assert.False(quote.Available);
        Assert.Equal("premium", quote.InquireTopic);
        Assert.NotNull(quote.Message);
    }

    [Fact]
    public async Task The_quote_refuses_what_checkout_would_refuse()
    {
        _user.SubscriptionTier = SubscriptionTier.Premium;
        _user.SubscriptionEndsAt = DateTimeOffset.UtcNow.AddDays(20);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Service(paymentsOn: true).QuoteAsync(new CheckoutRequest("venue-monthly", null, 1)));
        Assert.Equal("billing_premium_account", ex.ErrorCode);
    }

    [Fact]
    public async Task Paying_without_accepting_the_terms_is_refused_and_nothing_is_charged()
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Service(paymentsOn: true).CheckoutAsync(new CheckoutRequest("premium-monthly", null, 1)));

        Assert.Equal("billing_terms_not_accepted", ex.ErrorCode);
        Assert.Empty(_payments);
        await _provider.DidNotReceiveWithAnyArgs().CreateCheckoutSessionAsync(default!, default);
    }

    [Fact]
    public async Task Accepting_terms_that_have_since_changed_is_refused()
    {
        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Service(paymentsOn: true).CheckoutAsync(
            new CheckoutRequest("premium-monthly", null, 1, AcceptedTerms: true, TermsVersion: "2020-01-01")));

        Assert.Equal("billing_terms_changed", ex.ErrorCode);
        Assert.Empty(_payments);
    }

    [Fact]
    public async Task An_accepted_checkout_records_when_and_which_terms_were_accepted()
    {
        var before = DateTimeOffset.UtcNow;
        var result = await Service(paymentsOn: true).CheckoutAsync(
            new CheckoutRequest("premium-monthly", null, 1, AcceptedTerms: true, TermsVersion: LegalTerms.Version));

        Assert.True(result.Available);
        Assert.Equal("https://pay.example/sess-1", result.CheckoutUrl);
        var payment = Assert.Single(_payments);
        Assert.Equal(LegalTerms.Version, payment.TermsVersion);
        Assert.NotNull(payment.TermsAcceptedAt);
        Assert.True(payment.TermsAcceptedAt >= before);
        Assert.Equal("MVR", payment.Currency);
    }

    // ----- plans renew with the card they were bought with -----

    private CheckoutRequest Accepted(string item) => new(item, null, 1, AcceptedTerms: true, TermsVersion: LegalTerms.Version);

    [Fact]
    public async Task Buying_a_plan_saves_the_card_for_this_accounts_customer_and_tells_the_gateway_what_it_is()
    {
        _user.DisplayName = "Aisha";
        _recurring.CreateCustomerAsync("Aisha", "host@test.com", Arg.Any<CancellationToken>()).Returns("cus_7");

        await Service(paymentsOn: true).CheckoutAsync(Accepted("premium-monthly"));

        var payment = Assert.Single(_payments);
        Assert.True(payment.AutoRenew);
        Assert.Equal("cus_7", _user.PaymentCustomerId);
        await _provider.Received(1).CreateCheckoutSessionAsync(Arg.Is<CreateCheckoutSessionRequest>(r =>
            r.SaveCard && r.CustomerId == "cus_7" && r.PaymentId == payment.Id && r.Description == "Premium pass, a month"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_account_that_already_has_a_customer_keeps_it()
    {
        _user.PaymentCustomerId = "cus_old";

        await Service(paymentsOn: true).CheckoutAsync(Accepted("venue-monthly"));

        await _recurring.DidNotReceiveWithAnyArgs().CreateCustomerAsync(default!, default!, default);
        await _provider.Received(1).CreateCheckoutSessionAsync(Arg.Is<CreateCheckoutSessionRequest>(r => r.CustomerId == "cus_old"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task If_the_gateway_cant_make_a_customer_the_plan_is_still_bought_just_not_set_to_renew()
    {
        _recurring.CreateCustomerAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<string>(_ => throw new HttpRequestException("down"));

        var result = await Service(paymentsOn: true).CheckoutAsync(Accepted("premium-monthly"));

        Assert.True(result.Available);
        Assert.False(Assert.Single(_payments).AutoRenew);
    }

    [Fact]
    public async Task A_paid_plan_bought_with_its_card_saved_renews_by_itself_from_then_on()
    {
        _user.RenewalFailures = 2;
        var paid = new Payment
        {
            Id = Guid.NewGuid(), UserId = _user.Id, Kind = PaymentKind.PremiumMonthly, AutoRenew = true, Quantity = 1,
            Amount = 450m, Currency = "MVR", Status = PaymentStatus.Paid, Provider = "Fake", CreatedAt = DateTimeOffset.UtcNow,
        };
        _payments.Add(paid);

        await Service(paymentsOn: true).FulfilAsync(paid.Id);

        Assert.Equal(PaymentKind.PremiumMonthly, _user.AutoRenewKind);
        Assert.Equal(0, _user.RenewalFailures);
        Assert.Equal(SubscriptionTier.Premium, _user.SubscriptionTier);
    }

    [Fact]
    public async Task Stopping_automatic_renewal_leaves_the_plan_running_to_its_end()
    {
        var ends = DateTimeOffset.UtcNow.AddDays(12);
        _user.SubscriptionTier = SubscriptionTier.Premium;
        _user.SubscriptionEndsAt = ends;
        _user.AutoRenewKind = PaymentKind.PremiumMonthly;

        var account = await Service(paymentsOn: true).StopAutoRenewAsync();

        Assert.Null(_user.AutoRenewKind);
        Assert.Null(account.AutoRenew);
        Assert.True(account.Active);
        Assert.Equal(ends, _user.SubscriptionEndsAt);
    }

    [Fact]
    public async Task Landing_back_before_the_webhook_asks_the_gateway_and_applies_what_was_paid()
    {
        var pending = new Payment
        {
            Id = Guid.NewGuid(), UserId = _user.Id, Kind = PaymentKind.PremiumMonthly, Quantity = 1, Amount = 450m,
            Currency = "MVR", Status = PaymentStatus.Pending, Provider = "Fake", ProviderSessionId = "tx", CreatedAt = DateTimeOffset.UtcNow,
        };
        _payments.Add(pending);
        _paymentService.SyncAsync(pending.Id, Arg.Any<CancellationToken>()).Returns(_ =>
        {
            pending.Status = PaymentStatus.Paid;
            return new InvitesBlog.Application.Dtos.Payments.WebhookProcessResult(true, null, pending.Id);
        });

        var status = await Service(paymentsOn: true).PaymentStatusAsync(pending.Id);

        Assert.Equal("Paid", status.Status);
        Assert.Equal(SubscriptionTier.Premium, _user.SubscriptionTier);
        Assert.NotNull(pending.FulfilledAt);
    }

    [Fact]
    public async Task Someone_elses_payment_cant_be_looked_up()
    {
        var theirs = new Payment
        {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Kind = PaymentKind.PremiumMonthly, Amount = 450m, Currency = "MVR",
            Status = PaymentStatus.Paid, Provider = "Fake", CreatedAt = DateTimeOffset.UtcNow,
        };
        _payments.Add(theirs);

        await Assert.ThrowsAsync<NotFoundException>(() => Service(paymentsOn: true).PaymentStatusAsync(theirs.Id));
    }
}
