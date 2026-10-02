using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Dtos.Payments;

namespace InvitesBlog.Application.Services.Payments;

/// <summary>§10.5 Payments: checkout, top-up, and idempotent webhook processing. Dispatch of a campaign
/// (an Infrastructure concern) is signalled back to the controller via <see cref="WebhookProcessResult"/>.</summary>
public interface IPaymentService
{
    /// <summary>Initial campaign checkout (§4.7.2). Verifies ownership, prices, creates the payment + provider session.</summary>
    Task<CheckoutResponse> CheckoutAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>Capacity top-up checkout (§4.7.4). Verifies ownership; returns a message when no top-up is needed.</summary>
    Task<TopUpResponse> TopUpAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>Verifies + processes a provider webhook idempotently (§10.5). The primary way a payment's result arrives.</summary>
    Task<WebhookProcessResult> HandleWebhookAsync(string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct = default);

    /// <summary>
    /// Asks the gateway for one pending payment's state and records it: for a webhook that never
    /// arrived, and for the buyer landing back before it did. A payment already settled is left as is.
    /// </summary>
    Task<WebhookProcessResult> SyncAsync(Guid paymentId, CancellationToken ct = default);

    /// <summary>Records a result the gateway gave directly (a saved-card charge), exactly as a webhook's would be.</summary>
    Task<WebhookProcessResult> ApplyAsync(PaymentWebhookResult result, CancellationToken ct = default);

    /// <summary>Renders the local dev fake-checkout page (Fake provider only).</summary>
    string BuildDevCheckoutPage(string session, string payment, decimal amount, string success, string cancel);

    /// <summary>Simulates provider success from the dev checkout page by running the real webhook path.</summary>
    Task<WebhookProcessResult> CompleteDevCheckoutAsync(string session, string payment, CancellationToken ct = default);

    /// <summary>
    /// A return address that is safe to send a browser to: a path on this site, or an address on one
    /// of the app's own configured origins. Anything else — another host, a scheme-relative
    /// <c>//host</c>, <c>javascript:</c> — comes back as <c>/</c>.
    /// </summary>
    string SafeReturnUrl(string? url);
}
