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
    /// <summary><c>public</c> for gallery templates only; anything else (or unset) lists every visibility.</summary>
    public string? Visibility { get; set; }
}
