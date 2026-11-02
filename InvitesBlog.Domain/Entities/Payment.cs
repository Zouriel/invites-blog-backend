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
    public const string AppStore = "app_store";
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

/// <summary>
/// One App Store purchase, by its original transaction id: one-time items have one transaction, and a
/// subscription keeps its original id through every renewal, each renewal a <see cref="Payment"/> of its own.
/// Unlike Google Play there is nothing to settle at Apple: the app finishes the transaction once we answer.
/// </summary>
public sealed class AppStorePurchase
{
    public Guid Id { get; set; }
    public string OriginalTransactionId { get; set; } = default!;
    /// <summary>The latest transaction applied (a renewal has a new one).</summary>
    public string? TransactionId { get; set; }
    public string ProductId { get; set; } = default!;
    /// <summary>Whose it is, from the purchase intent. Null once that account has been deleted.</summary>
    public Guid? UserId { get; set; }
    /// <summary>The event it is bound to, from the purchase intent. Null for a subscription.</summary>
    public Guid? CampaignId { get; set; }
    /// <summary>The intent (appAccountToken) it was bought with.</summary>
    public Guid? IntentId { get; set; }
    /// <summary>Apple's sandbox: no money moved.</summary>
    public bool IsTest { get; set; }
    public Guid? PaymentId { get; set; }
    public DateTimeOffset? FulfilledAt { get; set; }
    /// <summary>Refunded or revoked; what it bought has been taken back.</summary>
    public DateTimeOffset? VoidedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    /// <summary>Subscription: Apple's status number (1 active … 5 revoked), as of <see cref="LastCheckedAt"/>.</summary>
    public int? SubscriptionStatus { get; set; }
    public bool AutoRenewing { get; set; }
    public DateTimeOffset? LastCheckedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// What a purchase is going to be for, made before StoreKit is asked. Its id goes into the purchase as
/// <c>appAccountToken</c> and comes back signed by Apple, which is how a transaction is tied to an account
/// and an event: StoreKit has no other field for it.
/// </summary>
public sealed class AppStoreIntent
{
    public Guid Id { get; set; }
    public Guid? UserId { get; set; }
    public Guid? CampaignId { get; set; }
    public string ProductId { get; set; } = default!;
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
