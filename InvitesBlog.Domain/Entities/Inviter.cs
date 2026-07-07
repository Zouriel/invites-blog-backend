namespace InvitesBlog.Domain.Entities;

/// <summary>
/// An inviter — no account, deduplicated by normalized email (§8.2 Inviter).
/// </summary>
public sealed class Inviter
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string PhoneE164 { get; set; } = default!;
    public string Email { get; set; } = default!;       // unique, lowercased
    public string? Organization { get; set; }
    public string? BillingName { get; set; }
    public string? BillingCountry { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
