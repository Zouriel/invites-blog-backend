using InvitesBlog.Application.Common;

namespace InvitesBlog.Application.Filters.Designers;

/// <summary>Query filter for the admin designer list (searchable by email / display name).</summary>
public sealed class DesignerFilter : PaginationRequest
{
    /// <summary>Null lists everyone; false lists only suspended accounts.</summary>
    public bool? IsActive { get; set; }
}
