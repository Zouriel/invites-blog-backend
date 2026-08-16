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
