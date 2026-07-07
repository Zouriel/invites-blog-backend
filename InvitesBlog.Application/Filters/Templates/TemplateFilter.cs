using InvitesBlog.Application.Common;

namespace InvitesBlog.Application.Filters.Templates;

/// <summary>
/// Query filter for the template gallery (spec §Filters — entity-specific filtering lives in its
/// own folder, never in the controller/service body).
/// </summary>
public sealed class TemplateFilter : PaginationRequest
{
    public string? Category { get; set; }
}
