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
