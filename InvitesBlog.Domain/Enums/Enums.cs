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
    TopUp
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
/// The review status machine a designer submission walks. Numeric values are pinned: the column is
/// persisted as an int, so members may be appended but never reordered.
/// </summary>
public enum CustomTemplateStatus
{
    /// <summary>Saved by the designer, not yet submitted for review.</summary>
    Draft = 0,
    Submitted = 1,
    InReview = 2,
    /// <summary>Approved AND promoted into a live gallery <c>Template</c>.</summary>
    Published = 3,
    Rejected = 4,
    Delisted = 5,
    /// <summary>Passed review; promotion into a <c>Template</c> row is the next step.</summary>
    Approved = 6
}

/// <summary>
/// What size of media bucket someone is on. The GB figure is the NAME of the tier, not the number
/// the system enforces — <see cref="Entities.MediaBucket.CapacityBytes"/> is frozen from it at
/// purchase so that renaming or repricing a tier cannot resize a bucket somebody already bought.
///
/// <para>Appended-only, and never reordered: EF stores these as ints, so moving one renames every
/// bucket already sold.</para>
/// </summary>
public enum MediaBucketTier
{
    /// <summary>What every event's box gets without anyone buying anything. No term, no bill.</summary>
    Free,
    Gb10,
    Gb20,
    Gb30,
    Gb50
}
