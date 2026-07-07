using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Domain.Entities;

/// <summary>A payment for a campaign (§8.2 Payment). Kind distinguishes Initial vs TopUp.</summary>
public sealed class Payment
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public PaymentKind Kind { get; set; }              // Initial | TopUp
    public int InviteCount { get; set; }
    public decimal Amount { get; set; }
    public string Currency { get; set; } = "USD";
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
