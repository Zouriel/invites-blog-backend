namespace InvitesBlog.Application.Dtos.Inquiries;

// ----- Public submit -----

/// <summary>The public "Start an inquiry" form.</summary>
public sealed record SubmitInquiryRequest(string Name, string Email, string Occasion, string Message);

public sealed record SubmitInquiryResponse(Guid Id);

// ----- Admin list / detail -----

public sealed record InquiryListItemDto(
    Guid Id, string Name, string Email, string Occasion,
    bool HasAttended, bool TemplateIssued, DateTimeOffset CreatedAt);

public sealed record InquiryDetailDto(
    Guid Id, string Name, string Email, string Occasion, string Message,
    string? Colors, string? References, string? Notes,
    bool HasAttended, DateTimeOffset? AttendedAt,
    bool TemplateIssued, DateTimeOffset? TemplateIssuedAt, Guid? IssuedTemplateId,
    DateTimeOffset CreatedAt,
    Guid? AssignedDesignerUserId, string? AssignedDesignerName,
    decimal? CommissionPrice, decimal? UsagePrice);

/// <summary>Owner-filled consultation fields + attended flag (colors/references/notes are all optional).</summary>
public sealed record UpdateInquiryRequest(string? Colors, string? References, string? Notes, bool HasAttended);

/// <summary>Hands a request to a designer at an agreed price (§Phase 5 commissions).</summary>
public sealed record AssignCommissionRequest(
    Guid? DesignerUserId, decimal? CommissionPrice, decimal? UsagePrice);

/// <summary>A commission as the designer it was handed to sees it.</summary>
public sealed record DesignerCommissionDto(
    Guid InquiryId, string RequesterName, string RequesterEmail, string Occasion, string Brief,
    string? Colors, string? References, string? Notes,
    decimal? CommissionPrice, decimal? UsagePrice,
    bool TemplateIssued, DateTimeOffset CreatedAt);

// ----- Issue a dedicated template for an inquiry -----

/// <summary>The packaged template data (produced in the API layer by the packager) the service persists.</summary>
public sealed record IssueTemplateData(
    string Name, string Slug, string Version, string Category, string? Description,
    string ManifestJson, string PackageUrl);

public sealed record InquiryIssuedResponse(Guid TemplateId, string Slug, bool Emailed);
