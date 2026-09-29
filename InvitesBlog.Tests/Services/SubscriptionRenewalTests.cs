using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Payments;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Application.Services.Payments;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The Premium pass and Venue renewing by themselves with the card saved when they were bought: charged from a
/// day before the plan ends, extended the way a payment by hand extends it, tried again a day after a
/// failure and stopped after three, and never charged twice for one renewal.
/// </summary>
public class SubscriptionRenewalTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly AppUser _user = new()
    {
        Id = Guid.NewGuid(), Email = "studio@test.com", DisplayName = "Studio person",
        SubscriptionTier = SubscriptionTier.Premium, SubscriptionEndsAt = Now.AddHours(12),
        AutoRenewKind = PaymentKind.PremiumMonthly, PaymentCustomerId = "cus_1",
    };
    private readonly List<Payment> _payments = [];
    private readonly IRecurringPaymentProvider _recurring = Substitute.For<IRecurringPaymentProvider>();
    private readonly IPaymentService _paymentService = Substitute.For<IPaymentService>();
    private readonly IBillingService _billing = Substitute.For<IBillingService>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();

    public SubscriptionRenewalTests()
    {
        _recurring.HasSavedCardAsync("cus_1", Arg.Any<CancellationToken>()).Returns(true);
        // What the payment service does with a result: a success marks the payment paid for billing.
        _paymentService.ApplyAsync(Arg.Any<PaymentWebhookResult>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var r = ci.Arg<PaymentWebhookResult>();
            if (r.Kind != WebhookEventKind.PaymentSucceeded) return new WebhookProcessResult(true, null);
            var p = _payments.Single(x => x.ProviderSessionId == r.ProviderSessionId);
            p.Status = PaymentStatus.Paid;
            return new WebhookProcessResult(true, null, p.Id);
        });
        // What billing does with a paid renewal: a month more.
        _billing.When(b => b.FulfilAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()))
            .Do(_ => _user.SubscriptionEndsAt = _user.SubscriptionEndsAt!.Value.AddMonths(1));
    }

    private SubscriptionRenewalService Sut()
    {
        var users = Substitute.For<IRepository<AppUser>>();
        users.Query(Arg.Any<bool>()).Returns(_ => new[] { _user }.AsAsyncQueryable());
        var payments = Substitute.For<IPaymentRepository>();
        payments.Query(Arg.Any<bool>()).Returns(_ => _payments.AsAsyncQueryable());
        payments.When(r => r.AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>())).Do(c => _payments.Add(c.Arg<Payment>()));
        var provider = Substitute.For<IPaymentProvider>();
        provider.Name.Returns("Bml");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls:InviterBase"] = "https://invites.blog",
        }).Build();
        return new SubscriptionRenewalService(users, payments, TestData.PriceBook(), provider, _recurring, _paymentService,
            _billing, _email, config, Substitute.For<IUnitOfWork>(), NullLogger<SubscriptionRenewalService>.Instance);
    }

    private void Charges(WebhookEventKind kind, string? nextAction = null) =>
        _recurring.ChargeSavedCardAsync(Arg.Any<SavedCardCharge>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var c = ci.Arg<SavedCardCharge>();
            return new PaymentWebhookResult(kind, $"tx_{Guid.NewGuid():N}", null, null, null)
            {
                Amount = c.Amount, Currency = c.Currency, LocalId = c.PaymentId.ToString(), NextActionUrl = nextAction,
            };
        });

    [Fact]
    public async Task A_plan_due_tomorrow_is_charged_at_the_price_book_price_extended_and_receipted()
    {
        Charges(WebhookEventKind.PaymentSucceeded);
        var endedAt = _user.SubscriptionEndsAt!.Value;

        await Sut().RunOnceAsync(Now);

        var payment = Assert.Single(_payments);
        Assert.Equal(PaymentKind.PremiumMonthly, payment.Kind);
        Assert.Equal(Prices.Defaults.PremiumMonthly, payment.Amount);
        Assert.Equal("MVR", payment.Currency);
        Assert.True(payment.AutoRenew);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        await _recurring.Received(1).ChargeSavedCardAsync(
            Arg.Is<SavedCardCharge>(c => c.CustomerId == "cus_1" && c.PaymentId == payment.Id && c.Amount == payment.Amount),
            Arg.Any<CancellationToken>());
        await _billing.Received(1).FulfilAsync(payment.Id, Arg.Any<CancellationToken>());
        Assert.Equal(endedAt.AddMonths(1), _user.SubscriptionEndsAt);
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.Subject.Contains("renewed")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_plan_not_yet_due_is_left_alone()
    {
        _user.SubscriptionEndsAt = Now.AddDays(5);

        await Sut().RunOnceAsync(Now);

        Assert.Empty(_payments);
        await _recurring.DidNotReceiveWithAnyArgs().ChargeSavedCardAsync(default!, default);
    }

    [Fact]
    public async Task A_plan_not_set_to_renew_is_left_alone()
    {
        _user.AutoRenewKind = null;

        await Sut().RunOnceAsync(Now);

        Assert.Empty(_payments);
    }

    [Fact]
    public async Task A_declined_card_is_retried_a_day_later_and_the_account_is_told()
    {
        Charges(WebhookEventKind.PaymentFailed);

        await Sut().RunOnceAsync(Now);

        Assert.Equal(1, _user.RenewalFailures);
        Assert.Equal(PaymentKind.PremiumMonthly, _user.AutoRenewKind);
        Assert.Equal(PaymentStatus.Failed, Assert.Single(_payments).Status);
        await _billing.DidNotReceiveWithAnyArgs().FulfilAsync(default, default);
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.Subject.Contains("couldn't renew")), Arg.Any<CancellationToken>());

        // An hour later: not yet.
        await Sut().RunOnceAsync(Now.AddHours(1));
        Assert.Single(_payments);

        // A day later: tried again.
        await Sut().RunOnceAsync(Now.AddDays(1));
        Assert.Equal(2, _payments.Count);
    }

    [Fact]
    public async Task Three_failures_in_a_row_turn_automatic_renewal_off()
    {
        Charges(WebhookEventKind.PaymentFailed);
        _user.RenewalFailures = 2;

        await Sut().RunOnceAsync(Now);

        Assert.Null(_user.AutoRenewKind);
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.Subject.Contains("could not renew")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_charge_the_bank_wants_confirmed_counts_as_a_failure()
    {
        Charges(WebhookEventKind.Ignored, nextAction: "https://3ds.bank/x");

        await Sut().RunOnceAsync(Now);

        Assert.Equal(1, _user.RenewalFailures);
        Assert.Equal(PaymentStatus.Failed, Assert.Single(_payments).Status);
    }

    [Fact]
    public async Task With_no_saved_card_renewal_turns_off_without_charging()
    {
        _recurring.HasSavedCardAsync("cus_1", Arg.Any<CancellationToken>()).Returns(false);

        await Sut().RunOnceAsync(Now);

        Assert.Null(_user.AutoRenewKind);
        Assert.Empty(_payments);
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.Subject.Contains("Renew your")), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_renewal_still_pending_at_the_gateway_is_never_charged_again()
    {
        _payments.Add(new Payment
        {
            Id = Guid.NewGuid(), UserId = _user.Id, Kind = PaymentKind.PremiumMonthly, AutoRenew = true,
            Status = PaymentStatus.Pending, Amount = 450m, Currency = "MVR", Provider = "Bml", CreatedAt = Now.AddHours(-2),
        });

        await Sut().RunOnceAsync(Now);

        Assert.Single(_payments);
        await _recurring.DidNotReceiveWithAnyArgs().ChargeSavedCardAsync(default!, default);
    }

    [Fact]
    public async Task A_plan_changed_by_hand_since_isnt_renewed_as_the_old_one()
    {
        _user.SubscriptionTier = SubscriptionTier.Venue;

        await Sut().RunOnceAsync(Now);

        Assert.Null(_user.AutoRenewKind);
        Assert.Empty(_payments);
    }

    [Fact]
    public async Task A_plan_lapsed_over_a_week_ago_isnt_brought_back()
    {
        _user.SubscriptionEndsAt = Now.AddDays(-10);

        await Sut().RunOnceAsync(Now);

        Assert.Empty(_payments);
    }
}
