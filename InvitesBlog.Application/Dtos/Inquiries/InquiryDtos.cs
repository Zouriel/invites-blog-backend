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
    DateTimeOffset CreatedAt);

/// <summary>Owner-filled consultation fields + attended flag (colors/references/notes are all optional).</summary>
public sealed record UpdateInquiryRequest(string? Colors, string? References, string? Notes, bool HasAttended);

// ----- Issue a dedicated template for an inquiry -----

/// <summary>The packaged template data (produced in the API layer by the packager) the service persists.</summary>
public sealed record IssueTemplateData(
    string Name, string Slug, string Version, string Category, string? Description,
    string ManifestJson, string PackageUrl);

public sealed record InquiryIssuedResponse(Guid TemplateId, string Slug, bool Emailed);
