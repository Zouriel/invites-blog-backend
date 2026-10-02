using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Infrastructure.Payments;

/// <summary>
/// A local, no-network payment provider for dev and tests (Payments__Provider=Fake). Checkout
/// returns a URL to the API's dev checkout page where a click simulates provider success by posting
/// a signed webhook to <c>/api/payments/webhook</c> — exercising the real idempotent webhook path
/// (§10.5 / §14) without Stripe. Replace with a real <see cref="IPaymentProvider"/> in production.
/// </summary>
public sealed class FakePaymentProvider(IConfiguration config) : IPaymentProvider, IRecurringPaymentProvider
{
    public string Name => "Fake";

    private string ApiBase => (config["Urls:ApiBase"] ?? "http://localhost:8080").TrimEnd('/');
    private string WebhookSecret => config["Payments:WebhookSecret"] ?? "fake-webhook-secret";

    public Task<CheckoutSessionResult> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest r, CancellationToken ct)
    {
        var sessionId = $"cs_fake_{Guid.NewGuid():N}";
        var paymentId = $"pi_fake_{Guid.NewGuid():N}";
        var url = $"{ApiBase}/api/dev/checkout" +
                  $"?session={Uri.EscapeDataString(sessionId)}" +
                  $"&payment={Uri.EscapeDataString(paymentId)}" +
                  $"&amount={r.Amount}" +
                  $"&success={Uri.EscapeDataString(r.SuccessUrl)}" +
                  $"&cancel={Uri.EscapeDataString(r.CancelUrl)}";
        return Task.FromResult(new CheckoutSessionResult(sessionId, url));
    }

    public Task<PaymentWebhookResult> HandleWebhookAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct) =>
        Task.FromResult(HandleWebhook(rawBody, headers.TryGetValue("X-Signature", out var sig) ? sig : null));

    /// <summary>The fake gateway has no API to ask: its payments are finished from the dev checkout page.</summary>
    public Task<PaymentWebhookResult> GetStatusAsync(string providerSessionId, CancellationToken ct) =>
        Task.FromResult(new PaymentWebhookResult(WebhookEventKind.Ignored, providerSessionId, null, null, null));

    // Saved cards, faked: every customer has a card and every charge goes through, so renewals can be
    // run end to end on a developer's machine.
    public Task<string> CreateCustomerAsync(string name, string email, CancellationToken ct) =>
        Task.FromResult($"cus_fake_{Guid.NewGuid():N}");

    public Task<bool> HasSavedCardAsync(string customerId, CancellationToken ct) => Task.FromResult(true);

    public Task<PaymentWebhookResult> ChargeSavedCardAsync(SavedCardCharge charge, CancellationToken ct) =>
        Task.FromResult(new PaymentWebhookResult(
            WebhookEventKind.PaymentSucceeded, $"cs_fake_{Guid.NewGuid():N}", $"pi_fake_{Guid.NewGuid():N}", null, null)
        {
            Amount = charge.Amount, Currency = charge.Currency, LocalId = charge.PaymentId.ToString(),
        });

    private PaymentWebhookResult HandleWebhook(string rawBody, string? signatureHeader)
    {
        // The dev checkout page signs the body with the shared secret (§14 webhook signature).
        if (signatureHeader != WebhookSecret)
            return new PaymentWebhookResult(WebhookEventKind.Unknown, null, null, null, null);

        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        var kind = type switch
        {
            "payment.succeeded" => WebhookEventKind.PaymentSucceeded,
            "payment.failed" => WebhookEventKind.PaymentFailed,
            "refund.succeeded" => WebhookEventKind.RefundSucceeded,
            _ => WebhookEventKind.Unknown
        };

        return new PaymentWebhookResult(
            Kind: kind,
            ProviderSessionId: Get(root, "sessionId"),
            ProviderPaymentId: Get(root, "paymentId"),
            ProviderRefundId: Get(root, "refundId"),
            IdempotencyKey: Get(root, "idempotencyKey"));
    }

    public Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct) =>
        Task.FromResult(new RefundResult(true, $"re_fake_{Guid.NewGuid():N}", null));

    private static string? Get(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
