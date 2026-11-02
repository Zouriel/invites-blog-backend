using InvitesBlog.Domain.Enums;
namespace InvitesBlog.Domain.Entities;

/// <summary>Record of an uploaded guest Excel file (§9.1 uploaded_guest_files).</summary>
public sealed class UploadedGuestFile
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public string FileName { get; set; } = default!;
    public string DefaultCountry { get; set; } = "MV";
    public int TotalRows { get; set; }
    public int ValidRows { get; set; }
    public int InvalidRows { get; set; }
    public int Duplicates { get; set; }
    public string ResultJson { get; set; } = "{}";     // full parse result for review screen
    public bool Confirmed { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Uploaded template asset (§9.1 template_assets).</summary>
public sealed class TemplateAsset
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public string Url { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public long SizeBytes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Uploaded campaign asset — cover images, logos (§9.1 campaign_assets).</summary>
public sealed class CampaignAsset
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public string Url { get; set; } = default!;
    public string ContentType { get; set; } = default!;
    public long SizeBytes { get; set; }
    public string? Slot { get; set; }                  // cover, couple, logo...
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Hashed suppression list — contacts who removed their data (§15.3).
/// Compared by hashed E.164 / lowercased email so future uploads cannot re-message them.
/// </summary>
public sealed class SuppressionEntry
{
    public Guid Id { get; set; }
    public string ContactHash { get; set; } = default!;   // unique
    public string ContactType { get; set; } = default!;   // "phone" | "email"
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Audit log entry for admin/inviter destructive actions (§9.1 audit_logs, §15.6).</summary>
public sealed class AuditLog
{
    public Guid Id { get; set; }
    public string Action { get; set; } = default!;
    public string? Actor { get; set; }
    public Guid? CampaignId { get; set; }
    public string DataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A proven email↔phone pairing for one person.
/// <para>
/// Guest rows already pair the two contacts, but that pairing is asserted by whoever uploaded the
/// list and is never checked — anyone can upload a stranger's email next to their own number. So an
/// uploaded pair is only ever a <em>hint</em>: it decides what we OFFER to link, never what someone
/// can see. A row lands here only after the person proved the second contact with a one-time code,
/// and only rows here widen an inbox.
/// </para>
/// </summary>
public sealed class VerifiedContactLink
{
    public Guid Id { get; set; }
    /// <summary>Lowercased email.</summary>
    public string Email { get; set; } = default!;
    /// <summary>E.164 phone.</summary>
    public string PhoneE164 { get; set; } = default!;
    /// <summary>Which side the person had already verified when they proved the other: "email" | "phone".</summary>
    public string VerifiedFrom { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Something outside the database still to be removed after an account was deleted: stored files and
/// the bank's customer record. Written in the same transaction as the deletion, so a crash can't lose
/// it, and worked through by a background service in the API host, so a failure is retried and a large
/// event doesn't hold up the request. Holds keys and references only.
/// </summary>
public sealed class PendingRemoval
{
    public Guid Id { get; set; }
    /// <summary>See <see cref="PendingRemovalKinds"/>.</summary>
    public string Kind { get; set; } = default!;
    public string Target { get; set; } = default!;
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public static class PendingRemovalKinds
{
    /// <summary>One object in public storage.</summary>
    public const string File = "file";
    /// <summary>Every object in public storage under a prefix.</summary>
    public const string Prefix = "prefix";
    /// <summary>One object in the private upload holding area.</summary>
    public const string IncomingFile = "incoming";
    /// <summary>A Google Play subscription to stop renewing, by its purchase token.</summary>
    public const string PlaySubscription = "play-subscription";
    /// <summary>A customer record at the payment gateway, with its saved cards.</summary>
    public const string PaymentCustomer = "payment-customer";
    /// <summary>A Sign in with Apple refresh token to revoke at Apple, still sealed (AppleTokenSeal).</summary>
    public const string AppleSignIn = "apple-signin";
}

/// <summary>
/// A video waiting to be made smaller, or to lose its camera metadata. Holds nothing a person typed: its
/// id is the photo's, and the row goes with the photo. See VideoTrimService.
/// </summary>
public sealed class VideoTrimJob
{
    /// <summary>The photo's id.</summary>
    public Guid Id { get; set; }
    public VideoTrimStatus Status { get; set; }
    /// <summary>True when what's in storage already has no camera metadata, so keeping it needs no work.</summary>
    public bool MetadataClean { get; set; }
    public int Attempts { get; set; }
    /// <summary>The file's size when it was queued, then what it became.</summary>
    public long OriginalBytes { get; set; }
    public long? ResultBytes { get; set; }
    /// <summary>In a few words why it ended as it did, for whoever is looking at the table.</summary>
    public string? Note { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum VideoTrimStatus
{
    Waiting = 0,
    Running = 1,
    /// <summary>A smaller copy replaced it.</summary>
    Trimmed = 2,
    /// <summary>The file stays as it is (or with only its metadata removed).</summary>
    Kept = 3,
    /// <summary>The photo went away first.</summary>
    Gone = 4,
}

/// <summary>A photograph on its way through the holding area (see DirectUploadService).</summary>
public sealed class PendingUpload
{
    /// <summary>Also the id of the photograph it becomes, so doing the work twice cannot make two.</summary>
    public Guid Id { get; set; }
    public Guid BucketId { get; set; }
    public Guid? CampaignId { get; set; }
    public Guid? GuestId { get; set; }
    public string? UploaderName { get; set; }
    /// <summary>The signed-in account the upload is from, carried to the photo it becomes.</summary>
    public Guid? UploaderUserId { get; set; }
    /// <summary>The QR code it came through, so the code's tally moves once it has really landed.</summary>
    public Guid? QrId { get; set; }
    public string IncomingKey { get; set; } = string.Empty;
    public string ContentType { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    /// <summary>What was reserved against the album, and the only size the upload link accepts.</summary>
    public long DeclaredBytes { get; set; }
    public PendingUploadStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
