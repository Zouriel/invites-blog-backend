using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Dtos.MediaBuckets;
/// <summary>
/// A bucket as its owner sees it in a list. Deliberately not the photographs — a grid of buckets
/// shows covers and how full each one is, and loading a thousand rows of media to draw that would
/// make the list cost what the box costs.
/// </summary>
public sealed record MediaBucketDto(
    Guid Id,
    /// <summary>
    /// What the OWNER calls this bucket — "The ceremony", "The after-party". Its own, because two
    /// buckets on one event would otherwise read identically.
    /// </summary>
    string Name,
    /// <summary>The event's name, shown WITH the bucket's rather than instead of it.</summary>
    string Title,
    /// <summary>The event's cover, chosen by the host on the campaign.</summary>
    string? CoverUrl,
    /// <summary>The event's plan: Free, Basic, EventPass or Premium.</summary>
    string Tier,
    /// <summary>The event's space in GB, to one decimal. Shared by all of the event's buckets.</summary>
    double Gb,
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
    /// <summary>How many days it collects for. 1 is the ordinary night.</summary>
    int WindowDays,
    /// <summary>
    /// Whether this is the event's first bucket — the free one the camera and the dashboard post to.
    /// </summary>
    bool IsDefault,
    DateTimeOffset? TermEndAt,
    /// <summary>Whether the paid term has run out. Always false on the free tier, which has no term.</summary>
    bool Expired,
    DateTimeOffset CreatedAt,
    int MaxBuckets = 1,
    int MaxWindowDays = 1,
    /// <summary>Active, UploadsClosed, OrganiserOnly or Deleted. See MediaPhase.</summary>
    string Phase = "Active",
    /// <summary>What all of the event's buckets hold together, against the event's space.</summary>
    long EventUsedBytes = 0,
    /// <summary>Whether this bucket's size can be set (Basic and Premium).</summary>
    bool Allocatable = false,
    /// <summary>The account's space on a subscription, and how much of it all its buckets are given.</summary>
    long? AccountBytes = null,
    long AccountAllocatedBytes = 0,
    /// <summary>The most this bucket's event can be given, and how much its buckets are given now.</summary>
    long EventMaxBytes = 0,
    long EventAllocatedBytes = 0);

/// <summary>An account's subscription space: how much it has, how much is given out, and how much is used.</summary>
public sealed record StorageSummaryDto(
    string Tier, long? AccountBytes, long AllocatedBytes, long UsedBytes, long EventMaxBytes);

/// <summary>Sets how many GB of the account's subscription space one bucket gets.</summary>
public sealed record SetBucketAllocationRequest(double Gb);

/// <summary>
/// Creating a bucket. <c>EventDate</c> is the night it is for and is required for a standalone one;
/// a bucket attached to a campaign takes that campaign's date and ignores whatever is posted here.
/// </summary>
/// <param name="WindowDays">
/// How many days it collects for, counted from when the event begins. Null or 1 is the ordinary
/// night; more is a subscriber's right and is capped at <c>EventDayWindow.MaxWindowDays</c>. Frozen
/// onto the bucket, so losing the subscription cannot close one already made.
/// </param>
public sealed record CreateMediaBucketRequest(
    string Title, string? Tier, Guid? CampaignId, DateTimeOffset? EventDate, int? WindowDays = null,
    /// <summary>What to call the bucket itself. Blank is the default name.</summary>
    string? Name = null);

/// <summary>
/// One of an event's buckets, as it appears in the panel for editing a guest — with whether that
/// guest may look into it.
/// </summary>
public sealed record BucketAccessGuestDto(Guid GuestId, string Name, IReadOnlyList<string> Roles, bool Allowed);

public sealed record BucketAccessDto(Guid BucketId, bool IsRestricted, IReadOnlyList<BucketAccessGuestDto> Guests);

/// <summary>Letting one guest into one bucket, or shutting them out of it.</summary>
public sealed record SetBucketAccessRequest(IReadOnlyList<Guid> GuestIds, bool Allowed);

/// <summary>Renaming a bucket. Blank falls back to the default rather than leaving a gap.</summary>
public sealed record RenameMediaBucketRequest(string Name);
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
