namespace InvitesBlog.Domain.Entities;

/// <summary>A campaign guest (§8.2 Guest). Identity is phone_e164 / email.</summary>
public sealed class Guest
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public string? Email { get; set; }
    public string? PhoneE164 { get; set; }
    public string? PhoneRaw { get; set; }
    public string Name { get; set; } = "Guest";
    public string? Role { get; set; }
    public string Gender { get; set; } = "unspecified";
    public string MetadataJson { get; set; } = "{}";
    public bool OptedOut { get; set; }                 // §15.3
    public DateTimeOffset CreatedAt { get; set; }
}
