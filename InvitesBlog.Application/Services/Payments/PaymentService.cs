using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Payments;
using InvitesBlog.Application.Exceptions.Campaigns;
using InvitesBlog.Application.Pricing;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Services.Payments;

/// <summary>
/// §10.5 Payment business logic. Checkout / top-up create a provider session and a pending payment;
/// the webhook path marks payments Paid, grows capacity and updates campaign status idempotently.
/// The actual campaign dispatch is delegated to the controller (Infrastructure boundary) via the
/// returned <see cref="WebhookProcessResult"/>.
/// </summary>
public sealed class PaymentService(
    ICampaignRepository campaigns,
    IPaymentRepository payments,
    IGuestRepository guests,
    IRepository<Refund> refunds,
    IUnitOfWork unitOfWork,
    IPaymentProvider provider,
    IConfiguration config,
    ICurrentUser currentUser) : IPaymentService
{
    // Injected per the slice contract; refunds are written on the cancel path (owned by another slice).
    private readonly IRepository<Refund> _refunds = refunds;

    private string InviterBase => (config["Urls:InviterBase"] ?? "http://localhost:4200").TrimEnd('/');
    private string WebhookSecret => config["Payments:WebhookSecret"] ?? "fake-webhook-secret";

    public async Task<CheckoutResponse> CheckoutAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await AuthorizeAsync(campaignId, ct);

        var guestCount = await guests.CountByCampaignAsync(campaignId, ct);
        if (guestCount == 0) throw new CampaignHasNoGuestsException();

        var inviteCount = Math.Max(guestCount, PricingCalculator.IncludedInvites);
        var price = PricingCalculator.CalculateInitial(inviteCount, campaign.HasDesignerDiscount);
        var capacity = price.IncludedInvites + price.ExtraBlocks * price.BlockSize;

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Kind = PaymentKind.Initial,
            InviteCount = capacity,
            Amount = price.Total,
            Currency = price.Currency,
            Status = PaymentStatus.Created,
            Provider = provider.Name,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var session = await provider.CreateCheckoutSessionAsync(new CreateCheckoutSessionRequest(
            campaignId, "Initial", price.Total, price.Currency, capacity,
            $"{InviterBase}/create/{campaignId}/success",
            $"{InviterBase}/create/{campaignId}/payment"), ct);

        payment.ProviderSessionId = session.SessionId;
        payment.Status = PaymentStatus.Pending;
        await payments.AddAsync(payment, ct);
        campaign.Status = CampaignStatus.PendingPayment;
        await unitOfWork.SaveChangesAsync(ct);

        return new CheckoutResponse(session.CheckoutUrl, price);
    }

    public async Task<TopUpResponse> TopUpAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await AuthorizeAsync(campaignId, ct);

        var guestCount = await guests.CountByCampaignAsync(campaignId, ct);
        var topUp = PricingCalculator.CalculateTopUp(
            campaign.PaidInviteCapacity, guestCount, 0, campaign.HasDesignerDiscount);
        if (topUp.ExtraBlocks == 0)
            return new TopUpResponse(null, null, "No top-up needed; capacity covers all guests.");

        var capacityAdded = topUp.ExtraBlocks * topUp.BlockSize;
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Kind = PaymentKind.TopUp,
            InviteCount = capacityAdded,
            Amount = topUp.Total,
            Currency = topUp.Currency,
            Status = PaymentStatus.Created,
            Provider = provider.Name,
            CreatedAt = DateTimeOffset.UtcNow
        };

        var session = await provider.CreateCheckoutSessionAsync(new CreateCheckoutSessionRequest(
            campaignId, "TopUp", topUp.Total, topUp.Currency, capacityAdded,
            $"{InviterBase}/dashboard/{campaignId}", $"{InviterBase}/dashboard/{campaignId}"), ct);

        payment.ProviderSessionId = session.SessionId;
        payment.Status = PaymentStatus.Pending;
        await payments.AddAsync(payment, ct);
        await unitOfWork.SaveChangesAsync(ct);

        return new TopUpResponse(session.CheckoutUrl, topUp, null);
    }

    public async Task<WebhookProcessResult> HandleWebhookAsync(string rawBody, string? signature, CancellationToken ct = default)
    {
        var evt = provider.HandleWebhook(rawBody, signature);
        return await ProcessAsync(evt, ct);
    }

    public string BuildDevCheckoutPage(string session, string payment, decimal amount, string success, string cancel)
    {
        var complete = $"/api/dev/checkout/complete?session={Uri.EscapeDataString(session)}&payment={Uri.EscapeDataString(payment)}&success={Uri.EscapeDataString(success)}";
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>Demo checkout</title>
            <style>body{font-family:system-ui;max-width:420px;margin:12vh auto;text-align:center}
            a.btn{display:block;padding:14px;border-radius:10px;text-decoration:none;margin:10px 0}
            .pay{background:#8a6d1a;color:#fff}.cancel{background:#eee;color:#333}</style></head>
            <body><h2>invites.blog demo checkout</h2>
            <p>Amount due: <strong>${{amount}}</strong></p>
            <a class="btn pay" href="{{complete}}">Simulate successful payment</a>
            <a class="btn cancel" href="{{cancel}}">Cancel</a>
            <p style="color:#999;font-size:12px">This page stands in for Stripe in local dev.</p>
            </body></html>
            """;
    }

    public async Task<WebhookProcessResult> CompleteDevCheckoutAsync(string session, string payment, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            type = "payment.succeeded",
            sessionId = session,
            paymentId = payment,
            idempotencyKey = $"evt_{session}"
        });
        // The fake provider signs with the shared webhook secret; a real provider ignores this dev path.
        var signature = provider.Name == "Fake" ? WebhookSecret : null;
        var evt = provider.HandleWebhook(body, signature);
        return await ProcessAsync(evt, ct);
    }

    /// <summary>Ownership + existence check for a campaign-scoped action (§ auth).</summary>
    private async Task<Campaign> AuthorizeAsync(Guid campaignId, CancellationToken ct)
    {
        if (currentUser.CampaignId != campaignId) throw new CampaignAccessDeniedException();
        return await campaigns.GetByIdAsync(campaignId, ct) ?? throw new CampaignNotFoundException(campaignId);
    }

    /// <summary>
    /// Idempotent webhook processing (§10.5): a payment already marked Paid short-circuits so a
    /// duplicate event never re-dispatches. Initial payments queue dispatch; top-ups grow capacity
    /// and (via the returned campaign id) send any not-yet-delivered guests.
    /// </summary>
    private async Task<WebhookProcessResult> ProcessAsync(PaymentWebhookResult evt, CancellationToken ct)
    {
        if (evt.Kind == WebhookEventKind.Unknown) return new WebhookProcessResult(false, null);

        var payment = await payments.GetBySessionIdAsync(evt.ProviderSessionId!, ct);
        if (payment is null) return new WebhookProcessResult(false, null);

        if (evt.Kind == WebhookEventKind.PaymentFailed)
        {
            payment.Status = PaymentStatus.Failed;
            var failedCampaign = await campaigns.GetByIdAsync(payment.CampaignId, ct);
            if (failedCampaign is not null && failedCampaign.Status == CampaignStatus.PendingPayment)
                failedCampaign.Status = CampaignStatus.PaymentFailed;
            await unitOfWork.SaveChangesAsync(ct);
            return new WebhookProcessResult(true, null);
        }

        if (evt.Kind != WebhookEventKind.PaymentSucceeded) return new WebhookProcessResult(true, null);

        // Idempotency: already processed.
        if (payment.Status == PaymentStatus.Paid) return new WebhookProcessResult(true, null);

        payment.Status = PaymentStatus.Paid;
        payment.PaidAt = DateTimeOffset.UtcNow;
        payment.ProviderPaymentId = evt.ProviderPaymentId;

        var campaign = await campaigns.GetByIdAsync(payment.CampaignId, ct);
        if (campaign is null)
        {
            await unitOfWork.SaveChangesAsync(ct);
            return new WebhookProcessResult(true, null);
        }

        if (payment.Kind == PaymentKind.Initial)
        {
            campaign.PaidInviteCapacity = payment.InviteCount;
            campaign.Status = CampaignStatus.DispatchQueued;   // §13.1 — only on initial payment
        }
        else // TopUp
        {
            campaign.PaidInviteCapacity += payment.InviteCount;  // sends only not-yet-delivered guests
        }

        await unitOfWork.SaveChangesAsync(ct);
        return new WebhookProcessResult(true, campaign.Id);
    }
}
