using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Domain.Entities;

/// <summary>
/// A payment: for an event (a pass, keeping its photos, emails) or for an account (Studio, Studio
/// pass credits). <see cref="Kind"/> says which; Initial and TopUp are the original sending payments.
/// </summary>
public sealed class Payment
{
    public Guid Id { get; set; }
    /// <summary>The event paid for; null for what an account buys (Studio, pass credits).</summary>
    public Guid? CampaignId { get; set; }
    /// <summary>Who is paying. Set for everything the billing page sells.</summary>
    public Guid? UserId { get; set; }
    public PaymentKind Kind { get; set; }
    /// <summary>How many: blocks of emails, pass credits. 1 for the rest.</summary>
    public int Quantity { get; set; } = 1;
    /// <summary>What it was, in words, for the receipt and the history.</summary>
    public string? Description { get; set; }
    /// <summary>When what was bought was applied. Paid but not fulfilled means something went wrong after payment.</summary>
    public DateTimeOffset? FulfilledAt { get; set; }
    public int InviteCount { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "MVR";
    public PaymentStatus Status { get; set; }
    public string Provider { get; set; } = default!;
    public string? ProviderSessionId { get; set; }
    public string? ProviderPaymentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? PaidAt { get; set; }
    /// <summary>When the buyer ticked "I agree" at checkout. The bank's card rules require that acceptance.</summary>
    public DateTimeOffset? TermsAcceptedAt { get; set; }
    /// <summary>Which version of the Terms, Refund and Privacy pages they accepted (<c>LegalTerms.Version</c>).</summary>
    public string? TermsVersion { get; set; }
    /// <summary>
    /// A plan bought with its card saved on file: once paid, the plan renews by itself
    /// (<see cref="AppUser.AutoRenewKind"/>). Also set on the renewals themselves.
    /// </summary>
    public bool AutoRenew { get; set; }
    /// <summary>A store's test purchase (Google's license testers): no money moved, and it counts for nothing in the accounts.</summary>
    public bool IsTest { get; set; }
    /// <summary>
    /// When a store subscription says this period ends. Its expiry is the store's to decide, so it
    /// replaces the month a card payment would add.
    /// </summary>
    public DateTimeOffset? PeriodEndsAt { get; set; }
}

/// <summary>The value of <see cref="Payment.Provider"/> for what isn't paid through the card gateway.</summary>
public static class PaymentProviders
{
    public const string GooglePlay = "google_play";
    /// <summary>ISO 4217's "no currency": a store payment whose amount the store hasn't told us yet. Never MVR.</summary>
    public const string UnknownCurrency = "XXX";
}

/// <summary>
/// One Google Play purchase, by its token: the token is what makes redeeming it happen once. For a
/// subscription the same token covers every renewal, and each renewal is a <see cref="Payment"/> of its own.
/// </summary>
public sealed class GooglePlayPurchase
{
    public Guid Id { get; set; }
    public string PurchaseToken { get; set; } = default!;
    public string ProductId { get; set; } = default!;
    /// <summary>Whose it is, as Google reported it. Null once that account has been deleted.</summary>
    public Guid? UserId { get; set; }
    /// <summary>The event it is bound to, as Google reported it. Null for a subscription.</summary>
    public Guid? CampaignId { get; set; }
    public bool IsTest { get; set; }
    public string? OrderId { get; set; }
    /// <summary>The payment for the latest period (a one-time purchase only ever has one).</summary>
    public Guid? PaymentId { get; set; }
    /// <summary>What it bought was applied, in the same transaction as this being set.</summary>
    public DateTimeOffset? FulfilledAt { get; set; }
    /// <summary>Consumed or acknowledged at Google. Only ever after <see cref="FulfilledAt"/>.</summary>
    public DateTimeOffset? SettledAt { get; set; }
    /// <summary>Refunded or revoked; what it bought has been taken back.</summary>
    public DateTimeOffset? VoidedAt { get; set; }
    /// <summary>Subscription: when the period Google reports ends.</summary>
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Subscription: Google's state, as of <see cref="LastCheckedAt"/>.</summary>
    public string? SubscriptionState { get; set; }
    /// <summary>Subscription: it will renew by itself.</summary>
    public bool AutoRenewing { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A refund linked to a payment (§8.2 Refund, §14.3).</summary>
public sealed class Refund
{
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public decimal Amount { get; set; }
    public RefundStatus Status { get; set; }
    public string? ProviderRefundId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
