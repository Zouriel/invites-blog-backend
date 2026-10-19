namespace InvitesBlog.Application.Abstractions;

/// <summary>Object storage for compiled template packages and campaign assets (§7.1).</summary>
/// <summary>
/// Downloads an image from somewhere else on the web: a profile picture a sign-in provider points at.
/// Returns null on any failure (timeout, not found, too large), since nothing that calls it may fail
/// because of it.
/// </summary>
public interface IRemoteImageFetcher
{
    Task<(byte[] Content, string ContentType)?> FetchAsync(Uri url, int maxBytes, CancellationToken ct = default);
}

public interface IStorageService
{
    /// <summary>Stores an object and returns its public URL.</summary>
    Task<string> PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default);
    /// <summary>Reads a stored object's bytes, or null if it doesn't exist.</summary>
    Task<byte[]?> GetAsync(string key, CancellationToken ct = default);
    string PublicUrl(string key);

    /// <summary>
    /// The key behind a URL this service handed out, or null for one it didn't (another host, a
    /// template asset). The inverse of <see cref="PublicUrl"/>.
    /// </summary>
    string? KeyFor(string url);

    /// <summary>Removes an object. Removing one that isn't there is not an error.</summary>
    Task DeleteAsync(string key, CancellationToken ct = default);
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
    string? RemovalLink = null,
    // Set for a save the date: the email offers this to the guest's calendar instead of a reply.
    Events.CalendarEntry? SaveTheDate = null);

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

/// <summary>
/// A delivery channel that may only be tried for some addresses (Viber's test-numbers-only mode). A
/// guest it can't send to goes straight to the next channel, with no failed attempt on record.
/// </summary>
public interface IAddressGatedProvider
{
    bool CanSendTo(string address);
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
    IReadOnlyDictionary<string, string>? Headers = null,
    IReadOnlyList<EmailAttachment>? Attachments = null);

/// <summary>A file sent with an email, such as a save the date's .ics.</summary>
public sealed record EmailAttachment(string Filename, byte[] Content, string ContentType);

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
    string CancelUrl)
{
    /// <summary>Our payment's id. The gateway keeps it on its transaction and gives it back, so a result can be matched to the payment it's for.</summary>
    public Guid? PaymentId { get; init; }
    /// <summary>What it is, in words the buyer sees on the gateway's page and receipt.</summary>
    public string? Description { get; init; }
    /// <summary>The gateway's customer for this buyer, when the card is to be saved for renewals.</summary>
    public string? CustomerId { get; init; }
    /// <summary>Keep the card on file (with <see cref="CustomerId"/>) so the subscription can renew by itself.</summary>
    public bool SaveCard { get; init; }
}

public sealed record CheckoutSessionResult(string SessionId, string CheckoutUrl);

public sealed record RefundRequest(string ProviderPaymentId, decimal Amount);
public sealed record RefundResult(bool Success, string? ProviderRefundId, string? Error);

/// <summary>
/// What a gateway says happened. <see cref="Ignored"/> is an event that is genuine but changes
/// nothing (a payment still waiting, a card saved): acknowledged, so the gateway stops resending it.
/// <see cref="Unknown"/> is one that could not be trusted or read.
/// </summary>
public enum WebhookEventKind { PaymentSucceeded, PaymentFailed, RefundSucceeded, Unknown, Ignored }

public sealed record PaymentWebhookResult(
    WebhookEventKind Kind,
    string? ProviderSessionId,
    string? ProviderPaymentId,
    string? ProviderRefundId,
    string? IdempotencyKey)
{
    /// <summary>The amount the gateway says was paid, when it says. Checked against the payment before it counts.</summary>
    public decimal? Amount { get; init; }
    public string? Currency { get; init; }
    /// <summary>The payment id we gave the gateway (<see cref="CreateCheckoutSessionRequest.PaymentId"/>), when it returns it.</summary>
    public string? LocalId { get; init; }
    /// <summary>A page the cardholder has to open to finish (3-D Secure) before a saved-card charge can complete.</summary>
    public string? NextActionUrl { get; init; }
}

/// <summary>Payment gateway abstraction (§14.1). Webhook input is decoupled from ASP.NET.</summary>
public interface IPaymentProvider
{
    string Name { get; }
    Task<CheckoutSessionResult> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest request, CancellationToken ct);

    /// <summary>
    /// Verifies and reads a webhook. A gateway whose notifications are only a hint (BML's) asks its
    /// API for the transaction's real state here, so what comes back is what the gateway itself says,
    /// not what the request body claims. Must be idempotent-friendly (§10.5).
    /// </summary>
    /// <param name="headers">The request's headers, case-insensitive: signatures travel there.</param>
    Task<PaymentWebhookResult> HandleWebhookAsync(string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct);

    /// <summary>
    /// The state of one checkout, asked of the gateway: for a webhook that never arrived, and for the
    /// buyer arriving back before it did. <see cref="WebhookEventKind.Ignored"/> means still waiting.
    /// </summary>
    Task<PaymentWebhookResult> GetStatusAsync(string providerSessionId, CancellationToken ct);

    Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct);
}

/// <summary>A charge to a card saved on file: a subscription renewing without the buyer present.</summary>
public sealed record SavedCardCharge(
    string CustomerId, decimal Amount, string Currency, Guid PaymentId, string Description);

/// <summary>
/// Cards kept on file for renewals. The gateway holds the card; we hold only its customer id.
/// </summary>
public interface IRecurringPaymentProvider
{
    /// <summary>Makes the gateway's customer record for a buyer, which their saved cards belong to.</summary>
    Task<string> CreateCustomerAsync(string name, string email, CancellationToken ct);

    /// <summary>Whether the customer has a card on file that can be charged.</summary>
    Task<bool> HasSavedCardAsync(string customerId, CancellationToken ct);

    /// <summary>
    /// Charges the customer's default saved card. The result's <c>ProviderSessionId</c> is the gateway's
    /// transaction, so a result that is still pending can be finished by the webhook or a status check.
    /// </summary>
    Task<PaymentWebhookResult> ChargeSavedCardAsync(SavedCardCharge charge, CancellationToken ct);
}

// ----- QR codes (media bucket contribution) -----

/// <summary>
/// Draws a QR code. A port rather than a call into a library, for the same reason storage is one:
/// the thing being drawn is a URL, and the application layer should not have to know which encoder
/// is on the other side of that.
/// </summary>
public interface IQrCodeRenderer
{
    /// <summary>
    /// The code for <paramref name="content"/> as a PNG.
    ///
    /// <para>PNG rather than SVG because of what happens to it: a QR for a party gets dropped into a
    /// table card in whatever someone already has open, printed, and photographed off a phone screen
    /// by a hundred people. A raster at print resolution survives that chain; an SVG is better on
    /// paper and worse everywhere else it will actually be pasted.</para>
    /// </summary>
    /// <param name="pixelsPerModule">
    /// How many pixels each square of the code gets. Drives the final size, and is why this is
    /// generous by default — a code scanned across a table needs the modules to survive both a
    /// printer and a camera.
    /// </param>
    byte[] Png(string content, int pixelsPerModule = 12);
}
