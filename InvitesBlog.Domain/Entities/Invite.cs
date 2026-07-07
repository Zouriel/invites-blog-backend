using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Domain.Entities;

/// <summary>A per-guest invite with a secure token (§8.2 Invite).</summary>
public sealed class Invite
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public Guid GuestId { get; set; }
    public string TokenHash { get; set; } = default!;
    public bool RequiresOtp { get; set; }              // sensitive campaigns
    public InviteStatus Status { get; set; }
    public RsvpStatus RsvpStatus { get; set; }
    public DateTimeOffset? ViewedAt { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>A stored RSVP response (§9.1 rsvp_responses).</summary>
public sealed class RsvpResponse
{
    public Guid Id { get; set; }
    public Guid InviteId { get; set; }
    public RsvpStatus Status { get; set; }
    public int? GuestCount { get; set; }
    public string? MealPreference { get; set; }
    public string? Comment { get; set; }
    public string? ArrivalTime { get; set; }
    public string? ContactNote { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
