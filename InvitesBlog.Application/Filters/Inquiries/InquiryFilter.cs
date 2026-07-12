using InvitesBlog.Application.Common;

namespace InvitesBlog.Application.Filters.Inquiries;

/// <summary>
/// Query filter for the admin inquiry queue — paging, free-text search over name/email/occasion, and a
/// pipeline <see cref="Status"/> tab: <c>all</c> (default), <c>unattended</c> (not yet met), or
/// <c>attended-unissued</c> (met but no template issued yet).
/// </summary>
public sealed class InquiryFilter : PaginationRequest
{
    public string? Status { get; set; }
}
