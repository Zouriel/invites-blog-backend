namespace InvitesBlog.Application.Abstractions;

/// <summary>Object storage for compiled template packages and campaign assets (§7.1).</summary>
public interface IStorageService
{
    /// <summary>Stores an object and returns its public URL.</summary>
    Task<string> PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default);
    string PublicUrl(string key);
}

// ----- Delivery (§4.8.4) -----

public sealed record InviteDeliveryMessage(
    string Channel,
    string RecipientAddress,
    string InviterName,
    string InviteLink,
    string MessageText,
    string? Subject = null,
    bool IsOtp = false,
    Guid? CampaignId = null,
    Guid? InviteId = null,
    string? InviterEmail = null,
    string? RemovalLink = null);

public sealed record DeliveryResult(bool Success, string? ProviderMessageId, string? Error)
{
    public static DeliveryResult Ok(string? id) => new(true, id, null);
    public static DeliveryResult Fail(string error) => new(false, null, error);
}

/// <summary>A delivery channel implementation (§4.8.4 IInviteDeliveryProvider).</summary>
public interface IInviteDeliveryProvider
{
    string Channel { get; }
    Task<DeliveryResult> SendAsync(InviteDeliveryMessage message, CancellationToken cancellationToken);
}

// ----- OTP + transactional email (§11.1) -----

public interface IOtpSender
{
    /// <summary>"sms" | "email" — matches OtpChannel.</summary>
    string Channel { get; }
    Task<DeliveryResult> SendCodeAsync(string recipient, string code, CancellationToken ct);
}

/// <summary>Which reputation stream / from-identity an email is sent on (provider guide §2.5).</summary>
public enum EmailStream
{
    /// <summary>no-reply@ — OTP codes, magic links, receipts.</summary>
    System,
    /// <summary>invites@ — guest invite delivery, cancellation notices.</summary>
    Invites
}

/// <summary>A single transactional email (provider guide §2.3).</summary>
public sealed record EmailMessage(
    string To,
    string Subject,
    string Html,
    EmailStream Stream = EmailStream.System,
    string? Text = null,
    string? ReplyTo = null,
    IReadOnlyList<KeyValuePair<string, string>>? Tags = null,
    IReadOnlyDictionary<string, string>? Headers = null);

public interface IEmailSender
{
    Task<DeliveryResult> SendAsync(EmailMessage message, CancellationToken ct);

    /// <summary>Convenience overload — sends on the System identity with no tags.</summary>
    Task<DeliveryResult> SendAsync(string to, string subject, string htmlBody, CancellationToken ct)
        => SendAsync(new EmailMessage(to, subject, htmlBody), ct);
}

// ----- Payments (§14.1) -----

public sealed record CreateCheckoutSessionRequest(
    Guid CampaignId,
    string Kind,
    decimal Amount,
    string Currency,
    int InviteCount,
    string SuccessUrl,
    string CancelUrl);

public sealed record CheckoutSessionResult(string SessionId, string CheckoutUrl);

public sealed record RefundRequest(string ProviderPaymentId, decimal Amount);
public sealed record RefundResult(bool Success, string? ProviderRefundId, string? Error);

public enum WebhookEventKind { PaymentSucceeded, PaymentFailed, RefundSucceeded, Unknown }

public sealed record PaymentWebhookResult(
    WebhookEventKind Kind,
    string? ProviderSessionId,
    string? ProviderPaymentId,
    string? ProviderRefundId,
    string? IdempotencyKey);

/// <summary>Payment gateway abstraction (§14.1). Webhook input is decoupled from ASP.NET.</summary>
public interface IPaymentProvider
{
    string Name { get; }
    Task<CheckoutSessionResult> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request, CancellationToken ct);
    /// <summary>Verifies the signature and parses the event. Must be idempotent-friendly (§10.5).</summary>
    PaymentWebhookResult HandleWebhook(string rawBody, string? signatureHeader);
    Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct);
}
