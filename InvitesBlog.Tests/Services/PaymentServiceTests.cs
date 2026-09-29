using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions.Campaigns;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.Payments;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class PaymentServiceTests
{
    private static readonly IReadOnlyDictionary<string, string> NoHeaders = new Dictionary<string, string>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly IPaymentRepository _payments = Substitute.For<IPaymentRepository>();
    private readonly IGuestRepository _guests = Substitute.For<IGuestRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IPaymentProvider _provider = Substitute.For<IPaymentProvider>();
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly IInviterRepository _inviters = Substitute.For<IInviterRepository>();

    public PaymentServiceTests() => _provider.Name.Returns("Fake");

    private PaymentService Sut() => new(
        new CampaignOwnershipService(_currentUser, _users, _campaigns, _inviters, TestData.NoCelebrants(), TestData.Empty<Venue>(), TestData.Empty<VenueStaff>()),
        _campaigns, _payments, _guests, _uow, _provider, _config, TestData.FreePlans(), TestData.PriceBook());

    private void Authorize(Campaign c)
    {
        _currentUser.CampaignId.Returns(c.Id);
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
    }

    // ----- Checkout -----

    [Fact]
    public async Task Checkout_access_denied_when_campaign_mismatch()
    {
        var c = TestData.Campaign();
        _currentUser.CampaignId.Returns(Guid.NewGuid()); // different campaign
        await Assert.ThrowsAsync<CampaignAccessDeniedException>(() => Sut().CheckoutAsync(c.Id));
    }

    [Fact]
    public async Task Checkout_missing_campaign_throws_NotFound()
    {
        var id = Guid.NewGuid();
        _currentUser.CampaignId.Returns(id);
        _campaigns.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((Campaign?)null);
        await Assert.ThrowsAsync<CampaignNotFoundException>(() => Sut().CheckoutAsync(id));
    }

    [Fact]
    public async Task Checkout_no_guests_throws()
    {
        var c = TestData.Campaign();
        Authorize(c);
        _guests.CountByCampaignAsync(c.Id, Arg.Any<CancellationToken>()).Returns(0);
        await Assert.ThrowsAsync<CampaignHasNoGuestsException>(() => Sut().CheckoutAsync(c.Id));
    }

    [Fact]
    public async Task Checkout_success_creates_pending_payment_and_session()
    {
        var c = TestData.Campaign();
        Authorize(c);
        _guests.CountByCampaignAsync(c.Id, Arg.Any<CancellationToken>()).Returns(30);
        _provider.CreateCheckoutSessionAsync(Arg.Any<CreateCheckoutSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CheckoutSessionResult("sess_1", "https://pay.test/sess_1"));

        var res = await Sut().CheckoutAsync(c.Id);

        Assert.Equal("https://pay.test/sess_1", res.CheckoutUrl);
        Assert.Equal(CampaignStatus.PendingPayment, c.Status);
        await _payments.Received(1).AddAsync(Arg.Is<Payment>(p => p.Status == PaymentStatus.Pending), Arg.Any<CancellationToken>());
    }

    // ----- TopUp -----

    [Fact]
    public async Task TopUp_no_topup_needed_returns_message()
    {
        var c = TestData.Campaign(paidCapacity: 50);
        Authorize(c);
        _guests.CountByCampaignAsync(c.Id, Arg.Any<CancellationToken>()).Returns(40); // within capacity

        var res = await Sut().TopUpAsync(c.Id);

        Assert.Null(res.CheckoutUrl);
        Assert.NotNull(res.Message);
        await _payments.DidNotReceive().AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TopUp_needed_creates_session()
    {
        var c = TestData.Campaign(paidCapacity: 50);
        Authorize(c);
        _guests.CountByCampaignAsync(c.Id, Arg.Any<CancellationToken>()).Returns(65); // exceeds capacity
        _provider.CreateCheckoutSessionAsync(Arg.Any<CreateCheckoutSessionRequest>(), Arg.Any<CancellationToken>())
            .Returns(new CheckoutSessionResult("sess_top", "https://pay.test/top"));

        var res = await Sut().TopUpAsync(c.Id);

        Assert.Equal("https://pay.test/top", res.CheckoutUrl);
        await _payments.Received(1).AddAsync(Arg.Any<Payment>(), Arg.Any<CancellationToken>());
    }

    // ----- Webhook processing -----

    [Fact]
    public async Task Webhook_unknown_event_not_handled()
    {
        _provider.HandleWebhookAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentWebhookResult(WebhookEventKind.Unknown, null, null, null, null));

        var res = await Sut().HandleWebhookAsync("{}", NoHeaders);

        Assert.False(res.Handled);
        Assert.Null(res.DispatchCampaignId);
    }

    [Fact]
    public async Task Webhook_unknown_payment_not_handled()
    {
        _provider.HandleWebhookAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, "sess_x", "pay_x", null, "k"));
        _payments.GetBySessionIdAsync("sess_x", Arg.Any<CancellationToken>()).Returns((Payment?)null);

        var res = await Sut().HandleWebhookAsync("{}", NoHeaders);

        Assert.False(res.Handled);
    }

    [Fact]
    public async Task Webhook_payment_failed_marks_campaign_payment_failed()
    {
        var c = TestData.Campaign(status: CampaignStatus.PendingPayment);
        var payment = TestData.Payment(c.Id, status: PaymentStatus.Pending, sessionId: "sess_f");
        _provider.HandleWebhookAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentWebhookResult(WebhookEventKind.PaymentFailed, "sess_f", null, null, "k"));
        _payments.GetBySessionIdAsync("sess_f", Arg.Any<CancellationToken>()).Returns(payment);
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);

        var res = await Sut().HandleWebhookAsync("{}", NoHeaders);

        Assert.True(res.Handled);
        Assert.Null(res.DispatchCampaignId);
        Assert.Equal(PaymentStatus.Failed, payment.Status);
        Assert.Equal(CampaignStatus.PaymentFailed, c.Status);
    }

    [Fact]
    public async Task Webhook_initial_success_queues_dispatch_and_sets_capacity()
    {
        var c = TestData.Campaign(status: CampaignStatus.PendingPayment);
        var payment = TestData.Payment(c.Id, kind: PaymentKind.Initial, status: PaymentStatus.Pending, inviteCount: 50, sessionId: "sess_i");
        _provider.HandleWebhookAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, "sess_i", "pay_i", null, "k"));
        _payments.GetBySessionIdAsync("sess_i", Arg.Any<CancellationToken>()).Returns(payment);
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);

        var res = await Sut().HandleWebhookAsync("{}", NoHeaders);

        Assert.True(res.Handled);
        Assert.Equal(c.Id, res.DispatchCampaignId);
        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal(50, c.PaidInviteCapacity);
        Assert.Equal(CampaignStatus.DispatchQueued, c.Status);
    }

    [Fact]
    public async Task Webhook_duplicate_paid_event_is_idempotent()
    {
        var c = TestData.Campaign();
        var payment = TestData.Payment(c.Id, status: PaymentStatus.Paid, sessionId: "sess_d");
        _provider.HandleWebhookAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, "sess_d", "pay_d", null, "k"));
        _payments.GetBySessionIdAsync("sess_d", Arg.Any<CancellationToken>()).Returns(payment);

        var res = await Sut().HandleWebhookAsync("{}", NoHeaders);

        Assert.True(res.Handled);
        Assert.Null(res.DispatchCampaignId); // already paid → no re-dispatch
    }

    [Fact]
    public async Task Webhook_topup_success_grows_capacity()
    {
        var c = TestData.Campaign(status: CampaignStatus.Dispatched, paidCapacity: 50);
        var payment = TestData.Payment(c.Id, kind: PaymentKind.TopUp, status: PaymentStatus.Pending, inviteCount: 10, sessionId: "sess_t");
        _provider.HandleWebhookAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, "sess_t", "pay_t", null, "k"));
        _payments.GetBySessionIdAsync("sess_t", Arg.Any<CancellationToken>()).Returns(payment);
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);

        var res = await Sut().HandleWebhookAsync("{}", NoHeaders);

        Assert.Equal(c.Id, res.DispatchCampaignId);
        Assert.Equal(60, c.PaidInviteCapacity); // 50 + 10
    }

    // ----- The gateway's word is checked against the payment -----

    private void Webhook(PaymentWebhookResult evt) =>
        _provider.HandleWebhookAsync(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<CancellationToken>())
            .Returns(evt);

    [Fact]
    public async Task A_confirmation_for_a_different_amount_does_not_mark_the_payment_paid()
    {
        var payment = TestData.Payment(Guid.NewGuid(), kind: PaymentKind.PartyPass, status: PaymentStatus.Pending, amount: 699m, sessionId: "tx_a");
        _payments.GetBySessionIdAsync("tx_a", Arg.Any<CancellationToken>()).Returns(payment);
        Webhook(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, "tx_a", "tx_a", null, "k") { Amount = 1m, Currency = "MVR" });

        var res = await Sut().HandleWebhookAsync("{}", NoHeaders);

        Assert.True(res.Handled);
        Assert.Null(res.FulfilPaymentId);
        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public async Task A_confirmation_in_another_currency_does_not_mark_the_payment_paid()
    {
        var payment = TestData.Payment(Guid.NewGuid(), kind: PaymentKind.PartyPass, status: PaymentStatus.Pending, amount: 699m, sessionId: "tx_c");
        _payments.GetBySessionIdAsync("tx_c", Arg.Any<CancellationToken>()).Returns(payment);
        Webhook(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, "tx_c", "tx_c", null, "k") { Amount = 699m, Currency = "USD" });

        await Sut().HandleWebhookAsync("{}", NoHeaders);

        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public async Task A_transaction_made_for_another_payment_is_ignored()
    {
        var payment = TestData.Payment(Guid.NewGuid(), kind: PaymentKind.PartyPass, status: PaymentStatus.Pending, amount: 699m, sessionId: "tx_l");
        _payments.GetBySessionIdAsync("tx_l", Arg.Any<CancellationToken>()).Returns(payment);
        Webhook(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, "tx_l", "tx_l", null, "k")
            { Amount = 699m, Currency = "MVR", LocalId = Guid.NewGuid().ToString() });

        await Sut().HandleWebhookAsync("{}", NoHeaders);

        Assert.Equal(PaymentStatus.Pending, payment.Status);
    }

    [Fact]
    public async Task The_exact_amount_for_this_payment_marks_it_paid_and_hands_it_to_billing()
    {
        var payment = TestData.Payment(Guid.NewGuid(), kind: PaymentKind.PartyPass, status: PaymentStatus.Pending, amount: 699m, sessionId: "tx_ok");
        _payments.GetBySessionIdAsync("tx_ok", Arg.Any<CancellationToken>()).Returns(payment);
        Webhook(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, "tx_ok", "tx_ok", null, "k")
            { Amount = 699m, Currency = "MVR", LocalId = payment.Id.ToString() });

        var res = await Sut().HandleWebhookAsync("{}", NoHeaders);

        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal(payment.Id, res.FulfilPaymentId);
    }

    [Fact]
    public async Task A_late_failure_never_undoes_a_payment_that_went_through()
    {
        var payment = TestData.Payment(Guid.NewGuid(), kind: PaymentKind.PartyPass, status: PaymentStatus.Paid, sessionId: "tx_p");
        _payments.GetBySessionIdAsync("tx_p", Arg.Any<CancellationToken>()).Returns(payment);
        Webhook(new PaymentWebhookResult(WebhookEventKind.PaymentFailed, "tx_p", null, null, "k"));

        await Sut().HandleWebhookAsync("{}", NoHeaders);

        Assert.Equal(PaymentStatus.Paid, payment.Status);
    }

    [Fact]
    public async Task A_genuine_event_with_nothing_to_record_is_acknowledged()
    {
        Webhook(new PaymentWebhookResult(WebhookEventKind.Ignored, "tx_w", null, null, null));

        var res = await Sut().HandleWebhookAsync("{}", NoHeaders);

        Assert.True(res.Handled);
        await _payments.DidNotReceiveWithAnyArgs().GetBySessionIdAsync(default!, default);
    }

    [Fact]
    public async Task Sync_asks_the_gateway_about_a_pending_payment_and_records_its_answer()
    {
        var payment = TestData.Payment(Guid.NewGuid(), kind: PaymentKind.PartyPass, status: PaymentStatus.Pending, amount: 199m, sessionId: "tx_s");
        _payments.Query(Arg.Any<bool>()).Returns(_ => new[] { payment }.AsAsyncQueryable());
        _payments.GetBySessionIdAsync("tx_s", Arg.Any<CancellationToken>()).Returns(payment);
        _provider.GetStatusAsync("tx_s", Arg.Any<CancellationToken>())
            .Returns(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, "tx_s", "tx_s", null, "k") { Amount = 199m, Currency = "MVR" });

        var res = await Sut().SyncAsync(payment.Id);

        Assert.Equal(PaymentStatus.Paid, payment.Status);
        Assert.Equal(payment.Id, res.FulfilPaymentId);
    }

    [Fact]
    public async Task Sync_leaves_a_settled_payment_alone()
    {
        var payment = TestData.Payment(Guid.NewGuid(), kind: PaymentKind.PartyPass, status: PaymentStatus.Paid, sessionId: "tx_done");
        _payments.Query(Arg.Any<bool>()).Returns(_ => new[] { payment }.AsAsyncQueryable());

        await Sut().SyncAsync(payment.Id);

        await _provider.DidNotReceiveWithAnyArgs().GetStatusAsync(default!, default);
    }
}
