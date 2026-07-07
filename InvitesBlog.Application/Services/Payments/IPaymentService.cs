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

    /// <summary>Verifies + processes a provider webhook idempotently (§10.5).</summary>
    Task<WebhookProcessResult> HandleWebhookAsync(string rawBody, string? signature, CancellationToken ct = default);

    /// <summary>Renders the local dev fake-checkout page (Fake provider only).</summary>
    string BuildDevCheckoutPage(string session, string payment, decimal amount, string success, string cancel);

    /// <summary>Simulates provider success from the dev checkout page by running the real webhook path.</summary>
    Task<WebhookProcessResult> CompleteDevCheckoutAsync(string session, string payment, CancellationToken ct = default);
}
