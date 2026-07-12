using InvitesBlog.Application.Common;

namespace InvitesBlog.Application.Filters.Templates;

/// <summary>
/// Admin template-management query: paging, free-text search (name/slug), a category filter, and a
/// <see cref="Status"/> tab — <c>active</c> (default), <c>inactive</c> (deactivated), or <c>all</c>.
/// </summary>
public sealed class AdminTemplateFilter : PaginationRequest
{
    public string? Category { get; set; }
    public string? Status { get; set; }
}
