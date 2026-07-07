using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Domain.Entities;

/// <summary>A single invitation campaign (§8.2 Campaign).</summary>
public sealed class Campaign
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public string TemplateVersion { get; set; } = default!;   // pinned (§5.6)
    public Guid? InviterId { get; set; }                      // set at inviter-details step
    public string AccessTokenHash { get; set; } = default!;   // §4.6.2 possession token
    public string? DashboardTokenHash { get; set; }           // §4.6.2 post-payment dashboard link
    public string Title { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public CampaignStatus Status { get; set; }
    public string EventType { get; set; } = "Other";
    public DateTimeOffset EventStartAt { get; set; }
    public DateTimeOffset? EventEndAt { get; set; }
    public int PaidInviteCapacity { get; set; }
    public bool HasDesignerDiscount { get; set; }
    public bool IsSensitive { get; set; }                     // §4.9.1 OTP-before-view
    public int RetentionDays { get; set; } = 90;              // §15.4
    public string CustomContentJson { get; set; } = "{}";
    public string ThemeOverridesJson { get; set; } = "{}";
    public string DeliverySettingsJson { get; set; } = "{}";
    public string RulesJson { get; set; } = "{\"rules\":[]}"; // §12 personalization rules
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
