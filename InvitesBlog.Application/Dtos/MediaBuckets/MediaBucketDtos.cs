using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Dtos.MediaBuckets;

/// <summary>One size of bucket as it is offered for sale.</summary>
public sealed record MediaBucketPlanDto(
    string Tier,
    int Gb,
    decimal Price,
    string Currency,
    int TermMonths,
    /// <summary>True for the tier this bucket is already on, so the picker can say so.</summary>
    bool IsCurrent);

/// <summary>
/// A bucket as its owner sees it in a list. Deliberately not the photographs — a grid of buckets
/// shows covers and how full each one is, and loading a thousand rows of media to draw that would
/// make the list cost what the box costs.
/// </summary>
public sealed record MediaBucketDto(
    Guid Id,
    /// <summary>The event's name. A bucket has none of its own.</summary>
    string Title,
    /// <summary>The event's cover, chosen by the host on the campaign.</summary>
    string? CoverUrl,
    string Tier,
    int Gb,
    long CapacityBytes,
    long UsedBytes,
    /// <summary>0–100, rounded, so every surface draws the same bar from the same number.</summary>
    int PercentUsed,
    int ItemCount,
    /// <summary>The event it collects for, when it has one.</summary>
    Guid? CampaignId,
    string? CampaignTitle,
    /// <summary>The night it is for. What decides when it is open.</summary>
    DateTimeOffset EventDate,
    /// <summary>Whether anything may be added right now — see EventDayWindow.</summary>
    bool IsOpen,
    DateTimeOffset? TermEndAt,
    /// <summary>Whether the paid term has run out. Always false on the free tier, which has no term.</summary>
    bool Expired,
    DateTimeOffset CreatedAt);

/// <summary>
/// Creating a bucket. <c>EventDate</c> is the night it is for and is required for a standalone one;
/// a bucket attached to a campaign takes that campaign's date and ignores whatever is posted here.
/// </summary>
public sealed record CreateMediaBucketRequest(
    string Title, string? Tier, Guid? CampaignId, DateTimeOffset? EventDate);

/// <summary>Moving a bucket onto a different size.</summary>
public sealed record ChooseMediaBucketTierRequest(string Tier);

/// <summary>
/// A QR code as the dashboard shows it.
///
/// <para><c>Url</c> is present ONLY in the response that created the code. It carries a bearer token
/// for writing into this bucket and is stored hashed, so the one moment it can be shown is the moment
/// it is made. Every later read returns null for it and carries <c>ImageUrl</c> instead — which is
/// exactly why the last code stays available to reprint without the secret being re-readable.</para>
/// </summary>
public sealed record MediaBucketQrDto(
    Guid Id,
    /// <summary>The scannable link. Present only in the response that created the code.</summary>
    string? Url,
    /// <summary>The rendered code. Always present — this is what the dashboard keeps on show.</summary>
    string ImageUrl,
    string? Label,
    bool AllowAnonymous,
    string TokenHint,
    int ScanCount,
    int UploadCount,
    bool Revoked,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset CreatedAt);

/// <summary>Generating a code: who it is for, and whether they have to say who they are.</summary>
public sealed record CreateMediaBucketQrRequest(string? Label, bool AllowAnonymous);
