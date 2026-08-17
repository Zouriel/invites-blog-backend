namespace InvitesBlog.Application.Dtos.Designers;

/// <summary>
/// A template submission the designer sends for review. The HTML and the preview image arrive as
/// uploaded files, so they're passed as raw content rather than in the JSON body.
/// </summary>
/// <param name="PublishedTemplateId">
/// Set when this submission EDITS an already-published template — approval then bumps that template's
/// version instead of creating a new one. Null for a brand-new template.
/// </param>
/// <param name="CommissionInquiryId">
/// Set when this answers a commission. The requester's email and the agreed prices are read from that
/// inquiry SERVER-SIDE after checking it was assigned to this designer — never taken from the client,
/// or a designer could reserve a template against someone else's email.
/// </param>
/// <param name="UsagePrice">
/// The per-use fee the designer proposes for a public template. Advisory: an admin sees it in the
/// review screen and it only takes effect once they approve.
/// </param>
public sealed record SubmitTemplateRequest(
    string Name,
    string Category,
    string Description,
    string Html,
    UploadedFile PreviewImage,
    Guid? PublishedTemplateId = null,
    Guid? CommissionInquiryId = null,
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
    DateTimeOffset UpdatedAt,
    /// <summary>The published template's visibility, once there is one. Null while unpublished.</summary>
    string? PublishedVisibility = null);

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

/// <summary>
/// The state of a commissioned template's release to the public gallery — what each party has agreed
/// to so far, and whether that's enough to make it public.
/// </summary>
public sealed record TemplateReleaseDto(
    Guid TemplateId,
    string Name,
    string Slug,
    string? PreviewImageUrl,
    string Visibility,
    string? RequestedByEmail,
    string? DesignerName,
    decimal? UsagePrice,
    bool RequesterConsentToPublish,
    bool DesignerConsentToPublish,
    bool IsPublic);

/// <summary>A designer as the admin list shows them, with what they've got in flight.</summary>
public sealed record DesignerAdminDto(
    Guid UserId,
    /// <summary>Null for an account that only ever signed in with a phone number.</summary>
    string? Email,
    string DisplayName,
    bool IsActive,
    IReadOnlyList<string> LinkedProviders,
    int PublishedTemplates,
    int PendingSubmissions,
    DateTimeOffset JoinedAt);

/// <summary>
/// One designer's earnings. Commissions are what was agreed for bespoke work; usage fees are the
/// per-use fee accrued each time an inviter started a campaign on one of their public templates.
/// </summary>
public sealed record DesignerEarningsDto(
    Guid UserId,
    string? Email,
    string DisplayName,
    decimal CommissionTotal,
    int CommissionCount,
    decimal UsageFeeTotal,
    int UsageFeeCampaigns,
    decimal Total,
    IReadOnlyList<DesignerTemplateEarningsDto> ByTemplate);

/// <summary>The per-template split behind a designer's usage-fee total.</summary>
public sealed record DesignerTemplateEarningsDto(
    Guid TemplateId, string Name, string Slug, decimal? UsagePrice, int Campaigns, decimal Total);

/// <summary>One row of the templates table.</summary>
/// <param name="CanEditDirectly">
/// True for an admin: their edit publishes immediately. For a designer it's false — their edit
/// becomes a submission for review, which the UI says plainly rather than pretending otherwise.
/// </param>
public sealed record MyTemplateRowDto(
    Guid Id,
    string Name,
    string Slug,
    string Category,
    string Version,
    string Visibility,
    bool IsActive,
    string? PreviewImageUrl,
    string? DesignerName,
    Guid? DesignerUserId,
    decimal? UsagePrice,
    decimal? CommissionPrice,
    int CampaignCount,
    bool CanEditDirectly,
    bool PendingReview,
    DateTimeOffset UpdatedAt);

/// <summary>The table plus the context the screen needs to title and explain itself.</summary>
/// <param name="Scope">"system" when an admin is looking at everything, "mine" for a designer's own.</param>
public sealed record MyTemplatesPageDto(
    string Scope, string Title, IReadOnlyList<MyTemplateRowDto> Templates);

/// <summary>A price change from the table. Null clears the fee.</summary>
public sealed record SetTemplatePricingRequest(decimal? UsagePrice, decimal? CommissionPrice);

/// <summary>What a delete actually did — unlisting is not the same as removing.</summary>
public sealed record DeleteTemplateResultDto(bool Deleted, bool Unlisted, int CampaignCount, string Message);
