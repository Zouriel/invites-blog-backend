using System.Globalization;
using System.Net;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Payments;
using InvitesBlog.Application.Exceptions.Campaigns;
using InvitesBlog.Application.Pricing;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Configuration;

using InvitesBlog.Application.Plans;
namespace InvitesBlog.Application.Services.Payments;

/// <summary>
/// §10.5 Payment business logic. Checkout / top-up create a provider session and a pending payment;
/// the webhook path marks payments Paid, grows capacity and updates campaign status idempotently.
/// The actual campaign dispatch is delegated to the controller (Infrastructure boundary) via the
/// returned <see cref="WebhookProcessResult"/>.
/// </summary>
public sealed class PaymentService(
    ICampaignOwnershipService ownership,
    ICampaignRepository campaigns,
    IPaymentRepository payments,
    IGuestRepository guests,
    IUnitOfWork unitOfWork,
    IPaymentProvider provider,
    IConfiguration config,
    IPlanService plans) : IPaymentService
{
    private string InviterBase => (config["Urls:InviterBase"] ?? "http://localhost:4200").TrimEnd('/');
    private string WebhookSecret => config["Payments:WebhookSecret"] ?? "fake-webhook-secret";

    public async Task<CheckoutResponse> CheckoutAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await AuthorizeAsync(campaignId, ct);

        var guestCount = await guests.CountByCampaignAsync(campaignId, ct);
        if (guestCount == 0) throw new CampaignHasNoGuestsException();

        var plan = await plans.ForCampaignAsync(campaignId, ct);
        var price = PricingCalculator.CalculateInitial(guestCount, plan.IncludedInvites);
        // What is bought is the EXTRA: the pass's included invitations are counted separately
        // (SendingAllowanceService), so PaidInviteCapacity only ever holds what was paid or given on top.
        var capacity = price.ExtraBlocks * price.BlockSize;

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
        var plan = await plans.ForCampaignAsync(campaignId, ct);
        // Covered: what the pass includes plus what was already added on top.
        var topUp = PricingCalculator.CalculateTopUp(
            plan.IncludedInvites + campaign.PaidInviteCapacity, guestCount, 0);
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
        // Every value that came from the query string is encoded on the way into the markup. The
        // cancel link is also run through SafeReturnUrl first, because encoding alone does nothing
        // for an href of javascript:… — it is still a script the moment somebody clicks Cancel.
        var complete = $"/api/dev/checkout/complete?session={Uri.EscapeDataString(session ?? "")}&payment={Uri.EscapeDataString(payment ?? "")}&success={Uri.EscapeDataString(SafeReturnUrl(success))}";
        var due = WebUtility.HtmlEncode(amount.ToString("0.00", CultureInfo.InvariantCulture));
        var pay = WebUtility.HtmlEncode(complete);
        var back = WebUtility.HtmlEncode(SafeReturnUrl(cancel));
        return $$"""
            <!doctype html><html><head><meta charset="utf-8"><title>Demo checkout</title>
            <style>body{font-family:system-ui;max-width:420px;margin:12vh auto;text-align:center}
            a.btn{display:block;padding:14px;border-radius:10px;text-decoration:none;margin:10px 0}
            .pay{background:#1b3d59;color:#fff}.cancel{background:#eee;color:#333}</style></head>
            <body><h2>invites.blog demo checkout</h2>
            <p>Amount due: <strong>{{PlanCatalog.Currency}} {{due}}</strong></p>
            <a class="btn pay" href="{{pay}}">Simulate successful payment</a>
            <a class="btn cancel" href="{{back}}">Cancel</a>
            <p style="color:#999;font-size:12px">This page stands in for the payment gateway in local dev.</p>
            </body></html>
            """;
    }

    public string SafeReturnUrl(string? url)
    {
        const string home = "/";
        if (string.IsNullOrWhiteSpace(url)) return home;

        // Browsers quietly drop tabs and newlines from a URL, so "/\t/evil.example" arrives as
        // "//evil.example". Anything carrying a control character or a backslash is refused outright
        // rather than cleaned — a legitimate return address never has one.
        if (url.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || c == '\\')) return home;

        // A path on this site. "//host" is scheme-relative, which is to say another site.
        if (url[0] == '/') return url.Length > 1 && url[1] == '/' ? home : url;

        // An absolute address on the app's own origins. Needed because the checkout's return address
        // is the INVITER app's dashboard, which in local development is a different port from the API
        // serving this page — a paths-only rule would strand the developer on the API's own root.
        if (Uri.TryCreate(url, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttps || absolute.Scheme == Uri.UriSchemeHttp)
            && AppOrigins.Contains(absolute.GetLeftPart(UriPartial.Authority), StringComparer.OrdinalIgnoreCase))
            return absolute.ToString();

        return home;
    }

    /// <summary>The two apps' origins, as configured. The only hosts a return address may name.</summary>
    private IEnumerable<string> AppOrigins =>
        new[] { InviterBase, config["Urls:InviteeBase"] ?? "http://localhost:4201" }
            .Select(b => Uri.TryCreate(b, UriKind.Absolute, out var u) ? u.GetLeftPart(UriPartial.Authority) : null)
            .OfType<string>();

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
        if (!await ownership.OwnsAsync(campaignId, ct)) throw new CampaignAccessDeniedException();
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
            campaign.PaidInviteCapacity += payment.InviteCount;
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
