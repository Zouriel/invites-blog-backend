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

    /// <summary>
    /// The first time invites.blog emailed this guest. What an event's emailed invitations are
    /// counted by: each guest once, so resending to someone already emailed costs nothing.
    /// </summary>
    public DateTimeOffset? FirstEmailedAt { get; set; }

    /// <summary>
    /// What paid for emailing this guest (2026-11-14). <see cref="EmailPaidBy.Event"/>: the event's pass or bought
    /// emails, counted once per guest, so sending again is free. <see cref="EmailPaidBy.Monthly"/>: the organiser's
    /// monthly allowance, where every email counts, sending again too (<see cref="MonthlyEmailSend"/>).
    /// </summary>
    public EmailPaidBy EmailPaidBy { get; set; }
    public DateTimeOffset? RespondedAt { get; set; }

    /// <summary>When the guest's phone was reminded the day before the event (EventReminderService); once.</summary>
    public DateTimeOffset? ReminderPushedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One of up to 3 IP addresses currently trusted to open a personal invite link (<c>/i/:token</c>)
/// without re-verifying via OTP. First-ever open of an invite auto-trusts that IP; opening from a
/// 4th distinct IP evicts whichever of the 3 has the oldest <see cref="LastSeenAt"/> to make room.
/// </summary>
public sealed class InviteTrustedIp
{
    public Guid Id { get; set; }
    public Guid InviteId { get; set; }
    public string IpAddress { get; set; } = default!;
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
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
    /// <summary>
    /// Answers to whatever the host asked beyond the four fields above, keyed by question. Those four
    /// keep their own columns because the dashboard and exports read them by name.
    /// </summary>
    public string AnswersJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One email charged to an account's monthly allowance of emailed invitations (Premium, Photographer, Studio;
/// 2026-11-14). The month is the calendar month in Maldives time (<c>EmailMonth</c>); the allowance is shared by
/// every event the account organises. A row is written before the email goes and removed if it isn't sent.
/// </summary>
public sealed class MonthlyEmailSend
{
    public Guid Id { get; set; }
    public Guid AccountId { get; set; }
    /// <summary>yyyymm, Maldives time.</summary>
    public int Month { get; set; }
    public Guid CampaignId { get; set; }
    public Guid InviteId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
