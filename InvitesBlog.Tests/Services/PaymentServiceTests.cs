using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions.Campaigns;
using InvitesBlog.Application.Services.Payments;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class PaymentServiceTests
{
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly IPaymentRepository _payments = Substitute.For<IPaymentRepository>();
    private readonly IGuestRepository _guests = Substitute.For<IGuestRepository>();
    private readonly IRepository<Refund> _refunds = Substitute.For<IRepository<Refund>>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IPaymentProvider _provider = Substitute.For<IPaymentProvider>();
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    public PaymentServiceTests() => _provider.Name.Returns("Fake");

    private PaymentService Sut() => new(
        _campaigns, _payments, _guests, _refunds, _uow, _provider, _config, _currentUser);

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
        _provider.HandleWebhook(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new PaymentWebhookResult(WebhookEventKind.Unknown, null, null, null, null));

        var res = await Sut().HandleWebhookAsync("{}", null);

        Assert.False(res.Handled);
        Assert.Null(res.DispatchCampaignId);
    }

    [Fact]
    public async Task Webhook_unknown_payment_not_handled()
    {
        _provider.HandleWebhook(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, "sess_x", "pay_x", null, "k"));
        _payments.GetBySessionIdAsync("sess_x", Arg.Any<CancellationToken>()).Returns((Payment?)null);

        var res = await Sut().HandleWebhookAsync("{}", null);

        Assert.False(res.Handled);
    }

    [Fact]
    public async Task Webhook_payment_failed_marks_campaign_payment_failed()
    {
        var c = TestData.Campaign(status: CampaignStatus.PendingPayment);
        var payment = TestData.Payment(c.Id, status: PaymentStatus.Pending, sessionId: "sess_f");
        _provider.HandleWebhook(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new PaymentWebhookResult(WebhookEventKind.PaymentFailed, "sess_f", null, null, "k"));
        _payments.GetBySessionIdAsync("sess_f", Arg.Any<CancellationToken>()).Returns(payment);
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);

        var res = await Sut().HandleWebhookAsync("{}", null);

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
        _provider.HandleWebhook(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, "sess_i", "pay_i", null, "k"));
        _payments.GetBySessionIdAsync("sess_i", Arg.Any<CancellationToken>()).Returns(payment);
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);

        var res = await Sut().HandleWebhookAsync("{}", null);

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
        _provider.HandleWebhook(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, "sess_d", "pay_d", null, "k"));
        _payments.GetBySessionIdAsync("sess_d", Arg.Any<CancellationToken>()).Returns(payment);

        var res = await Sut().HandleWebhookAsync("{}", null);

        Assert.True(res.Handled);
        Assert.Null(res.DispatchCampaignId); // already paid → no re-dispatch
    }

    [Fact]
    public async Task Webhook_topup_success_grows_capacity()
    {
        var c = TestData.Campaign(status: CampaignStatus.Dispatched, paidCapacity: 50);
        var payment = TestData.Payment(c.Id, kind: PaymentKind.TopUp, status: PaymentStatus.Pending, inviteCount: 10, sessionId: "sess_t");
        _provider.HandleWebhook(Arg.Any<string>(), Arg.Any<string?>())
            .Returns(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, "sess_t", "pay_t", null, "k"));
        _payments.GetBySessionIdAsync("sess_t", Arg.Any<CancellationToken>()).Returns(payment);
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);

        var res = await Sut().HandleWebhookAsync("{}", null);

        Assert.Equal(c.Id, res.DispatchCampaignId);
        Assert.Equal(60, c.PaidInviteCapacity); // 50 + 10
    }
}
