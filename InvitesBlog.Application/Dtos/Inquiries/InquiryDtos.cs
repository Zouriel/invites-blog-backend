namespace InvitesBlog.Application.Dtos.Inquiries;

// ----- Public submit -----

/// <summary>The public "Start an inquiry" form.</summary>
public sealed record SubmitInquiryRequest(string Name, string Email, string Occasion, string Message);

public sealed record SubmitInquiryResponse(Guid Id);

// ----- Admin list / detail -----

public sealed record InquiryListItemDto(
    Guid Id, string Name, string Email, string Occasion, bool HasAttended, DateTimeOffset CreatedAt);

public sealed record InquiryDetailDto(
    Guid Id, string Name, string Email, string Occasion, string Message,
    string? Colors, string? References, string? Notes,
    bool HasAttended, DateTimeOffset? AttendedAt,
    DateTimeOffset CreatedAt);

/// <summary>Owner-filled consultation fields + attended flag (colors/references/notes are all optional).</summary>
public sealed record UpdateInquiryRequest(string? Colors, string? References, string? Notes, bool HasAttended);
