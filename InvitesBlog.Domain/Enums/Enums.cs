namespace InvitesBlog.Domain.Enums;

public enum CampaignStatus
{
    Draft,
    PendingPayment,
    Paid,
    PaymentFailed,
    DispatchQueued,
    Dispatching,
    Dispatched,
    PartiallyDispatched,
    Cancelled,
    Refunded,
    PartiallyRefunded,
    DispatchFailed   // paid, but every delivery failed — appended; EF stores ints
}

/// <summary>
/// What a campaign sends. A save the date goes out months ahead with just the day: no album, no
/// camera, no replies — guests add it to their calendar, and the invitation follows as its own
/// campaign made from it (<see cref="Entities.Campaign.InvitationCampaignId"/>).
/// </summary>
public enum CampaignKind
{
    Invitation,
    SaveTheDate,
}

public enum InviteStatus
{
    Created,
    Queued,
    Sent,
    Failed,
    Viewed,
    Cancelled,
    NotSent   // no deliverable contact (no phone for Viber and no email) — appended; EF stores ints
}

public enum RsvpStatus
{
    NoResponse,
    Going,
    Maybe,
    NotGoing,
    ViewedOnly
}

public enum DeliveryStatus
{
    Pending,
    Sent,
    Delivered,
    Failed,
    Bounced,
    Skipped   // no channel was attemptable for the guest — appended; EF stores ints
}

public enum PaymentKind
{
    Initial,
    TopUp,
    // What the billing page sells (appended; EF stores ints). See BillingService.
    PartyPass,
    WeddingPass,
    KeepPhotos,
    Sending,
    StudioMonthly,
    StudioYearly,
    StudioPartyCredits,
    StudioWeddingCredits,
}

public enum PaymentStatus
{
    Created,
    Pending,
    Paid,
    Failed,
    Refunded,
    PartiallyRefunded
}

public enum RefundStatus
{
    Created,
    Pending,
    Succeeded,
    Failed
}

public enum OtpChannel
{
    Sms,
    Email
}

/// <summary>
/// What a code was sent FOR. Not a label — it partitions the per-contact send budget, so that
/// re-proving yourself on a personal invite link cannot exhaust the allowance you need to sign in,
/// or the other way round. Never settable by a caller: the service that starts the flow decides.
/// Numeric values are pinned — the column is persisted as an int, so members may be appended but
/// never reordered.
/// </summary>
public enum OtpPurpose
{
    /// <summary>Signing in — the inbox, and the shared campaign link.</summary>
    SignIn = 0,

    /// <summary>Re-proving a personal invite link from a network it doesn't recognise.</summary>
    InviteReauth = 1
}

/// <summary>
/// Where a report against a published template stands: open until an admin handles it (dismisses it,
/// or unlists / removes the template). Numeric values are pinned: the column is persisted as an int,
/// so members may be appended but never reordered.
/// </summary>
public enum TemplateReportStatus
{
    Open = 0,
    Resolved = 1
}

/// <summary>
/// LEGACY: the per-bucket sizes from before per-event plans (2026-09-15). Only read for buckets made
/// before <c>PlanCatalog.IntroducedAt</c>, which keep the space they had; nothing new is created with
/// anything but Free. Space now comes from the event's plan.
///
/// <para>Appended-only, and never reordered: EF stores these as ints.</para>
/// </summary>
public enum MediaBucketTier
{
    /// <summary>Every bucket made since per-event plans: its space is the event's plan's.</summary>
    Free,
    Gb10,
    Gb20,
    Gb30,
    Gb50
}

/// <summary>
/// An account's plan for professionals. Hosts buy a pass per event instead (<see cref="EventPassKind"/>).
/// The numbers are stored: 1 was Basic, retired in the 2026-09 plans, and 2 was Premium, which became Studio.
/// </summary>
public enum SubscriptionTier
{
    None = 0,
    /// <summary>Designers and planners: their clients' events in one place, a credit on the invitations they
    /// design, and passes at a discount to give to clients.</summary>
    Studio = 2,
    /// <summary>A resort or hall: albums for every event at the property, under its own name, run by its staff.</summary>
    Venue = 3,
}

/// <summary>What one event was bought. Stored as a number; see <c>Campaign.EventPass</c>.</summary>
public enum EventPassKind
{
    None = 0,
    Party = 1,
    Wedding = 2,
}
