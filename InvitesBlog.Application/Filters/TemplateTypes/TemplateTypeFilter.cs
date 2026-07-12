using InvitesBlog.Application.Common;

namespace InvitesBlog.Application.Filters.TemplateTypes;

/// <summary>Admin template-types query — paging + free-text search over name/slug.</summary>
public sealed class TemplateTypeFilter : PaginationRequest;
