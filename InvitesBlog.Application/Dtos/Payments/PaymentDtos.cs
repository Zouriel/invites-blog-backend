using System.Text.Json.Serialization;
using InvitesBlog.Application.Pricing;

namespace InvitesBlog.Application.Dtos.Payments;

/// <summary>Initial-checkout response (§10.5). Same JSON shape the Angular app expects: <c>{ checkoutUrl, price }</c>.</summary>
public sealed record CheckoutResponse(string CheckoutUrl, PriceBreakdown Price);

/// <summary>
/// Top-up response (§4.7.4). Either <c>{ checkoutUrl, price }</c> when a top-up is needed, or
/// <c>{ message }</c> when existing capacity already covers every guest. Null members are omitted so
/// the wire shape matches the legacy endpoint exactly.
/// </summary>
public sealed record TopUpResponse(
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? CheckoutUrl,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] PriceBreakdown? Price,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Message);

/// <summary>Acknowledgement returned to the payment provider after a webhook is handled: <c>{ received: true }</c>.</summary>
public sealed record WebhookAckResponse(bool Received);

/// <summary>
/// Outcome of idempotent webhook processing (§10.5) that the controller maps to HTTP + dispatch.
/// <see cref="Handled"/> is false only for unknown/invalid events (→ 400). <see cref="DispatchCampaignId"/>
/// is set only when a newly-Paid payment should trigger dispatch — a duplicate (already-Paid) event
/// leaves it null so the controller never re-dispatches. The dispatch call itself lives in the Api
/// layer because <c>DispatchService</c> is an Infrastructure type the Application layer cannot reference.
/// </summary>
public sealed record WebhookProcessResult(bool Handled, Guid? DispatchCampaignId);
