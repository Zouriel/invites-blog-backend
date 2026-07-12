using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Payments;
using InvitesBlog.Application.Exceptions.Payments;
using InvitesBlog.Application.Services.Payments;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Infrastructure.Delivery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// §10.5 Payments: checkout, top-up, the idempotent provider webhook, and the local dev fake-checkout
/// pages. Thin controller — <see cref="IPaymentService"/> holds the logic; the controller owns the
/// Infrastructure boundary (reading the raw webhook body and calling <see cref="DispatchService"/>,
/// which the Application layer cannot reference).
/// </summary>
public sealed class PaymentsController(IPaymentService payments, DispatchService dispatch) : BaseApiController
{
    // POST /api/campaigns/{id}/checkout [campaign-token]
    [HttpPost("/api/campaigns/{id:guid}/checkout")]
    [HasPermission(Permissions.Campaigns.Checkout)]
    public async Task<IActionResult> Checkout(Guid id, CancellationToken ct) =>
        Success(await payments.CheckoutAsync(id, ct));

    // POST /api/campaigns/{id}/topup [campaign-token] — checkout for added capacity (§4.7.4)
    [HttpPost("/api/campaigns/{id:guid}/topup")]
    [HasPermission(Permissions.Campaigns.Checkout)]
    public async Task<IActionResult> TopUp(Guid id, CancellationToken ct) =>
        Success(await payments.TopUpAsync(id, ct));

    // POST /api/payments/webhook (provider-signed, idempotent) — §10.5
    [HttpPost("/api/payments/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> Webhook(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        var signature = Request.Headers["X-Signature"].FirstOrDefault();

        var result = await payments.HandleWebhookAsync(body, signature, ct);
        if (!result.Handled) throw new PaymentWebhookInvalidException();
        if (result.DispatchCampaignId is Guid campaignId)
            // Use None, not the request token: a payment provider disconnecting/retrying must not
            // cancel a partially-completed dispatch (the retry short-circuits on Status==Paid and
            // would leave the campaign stuck in Dispatching with only some guests sent).
            await dispatch.DispatchCampaignAsync(campaignId, CancellationToken.None);

        return Success(new WebhookAckResponse(true));
    }

    // ---- Dev fake-checkout page (only meaningful with the Fake provider) ----

    [HttpGet("/api/dev/checkout")]
    [AllowAnonymous]
    public IActionResult DevCheckout(
        [FromQuery] string session, [FromQuery] string payment, [FromQuery] decimal amount,
        [FromQuery] string success, [FromQuery] string cancel) =>
        Content(payments.BuildDevCheckoutPage(session, payment, amount, success, cancel), "text/html");

    [HttpGet("/api/dev/checkout/complete")]
    [AllowAnonymous]
    public async Task<IActionResult> DevCheckoutComplete(
        [FromQuery] string session, [FromQuery] string payment, [FromQuery] string success, CancellationToken ct)
    {
        var result = await payments.CompleteDevCheckoutAsync(session, payment, ct);
        if (result.DispatchCampaignId is Guid campaignId)
            await dispatch.DispatchCampaignAsync(campaignId, ct);
        return Redirect(success);
    }
}
