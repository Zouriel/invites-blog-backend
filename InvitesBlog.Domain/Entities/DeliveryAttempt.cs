using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Domain.Entities;

/// <summary>A single delivery attempt for an invite (§8.2 DeliveryAttempt).</summary>
public sealed class DeliveryAttempt
{
    public Guid Id { get; set; }
    public Guid InviteId { get; set; }
    public string Channel { get; set; } = default!;
    public string RecipientAddress { get; set; } = default!;
    public DeliveryStatus Status { get; set; }
    public string? ProviderMessageId { get; set; }
    public string? ErrorMessage { get; set; }
    public bool IsOtp { get; set; }                    // §11.1 OTP SMS spend tracked separately
    public DateTimeOffset AttemptedAt { get; set; }
}
