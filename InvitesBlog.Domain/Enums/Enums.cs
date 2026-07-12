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
    PartiallyRefunded
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

public enum CustomTemplateStatus
{
    Private,
    Submitted,
    InReview,
    Published,
    Rejected,
    Delisted
}
