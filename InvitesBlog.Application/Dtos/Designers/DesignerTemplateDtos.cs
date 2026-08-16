namespace InvitesBlog.Application.Dtos.Designers;

/// <summary>
/// A template submission the designer sends for review. The HTML and the preview image arrive as
/// uploaded files, so they're passed as raw content rather than in the JSON body.
/// </summary>
/// <param name="PublishedTemplateId">
/// Set when this submission EDITS an already-published template — approval then bumps that template's
/// version instead of creating a new one. Null for a brand-new template.
/// </param>
public sealed record SubmitTemplateRequest(
    string Name,
    string Category,
    string Description,
    string Html,
    UploadedFile PreviewImage,
    Guid? PublishedTemplateId = null,
    string? RequestedByEmail = null,
    decimal? CommissionPrice = null,
    decimal? UsagePrice = null);

/// <summary>An uploaded file's bytes plus what it claims to be.</summary>
public sealed record UploadedFile(string FileName, string ContentType, byte[] Content);

/// <summary>One submission as its designer sees it.</summary>
public sealed record DesignerTemplateDto(
    Guid Id,
    string Name,
    string Slug,
    string Category,
    string Description,
    string Status,
    string? RejectionReason,
    string? PreviewImageUrl,
    string? PackageUrl,
    string ManifestJson,
    Guid? PublishedTemplateId,
    decimal? CommissionPrice,
    decimal? UsagePrice,
    string? RequestedByEmail,
    bool RequesterConsentToPublish,
    bool DesignerConsentToPublish,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>
/// One submission as the admin review queue sees it — adds who submitted it and the raw source, which
/// is what the reviewer's code view renders.
/// </summary>
public sealed record TemplateSubmissionDto(
    DesignerTemplateDto Template,
    Guid DesignerUserId,
    string DesignerEmail,
    string DesignerName,
    string Html);

/// <summary>An approve/reject decision. A rejection must say why.</summary>
public sealed record ReviewSubmissionRequest(bool Approve, string? RejectionReason);

/// <summary>The result of the automatic scan, surfaced to the designer before they submit.</summary>
public sealed record TemplateScanResultDto(
    bool Passed,
    string? ErrorCode,
    string? Error,
    int Bytes,
    int RecommendedBytes,
    int MaxBytes,
    bool OverRecommendedBudget,
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> ImageSlots,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> ThemeKeys);
