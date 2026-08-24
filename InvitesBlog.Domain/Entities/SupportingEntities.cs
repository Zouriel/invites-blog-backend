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
