using InvitesBlog.Application.Common;

namespace InvitesBlog.Application.Filters.Designers;

/// <summary>Query filter for the admin review queue, optionally scoped to one status.</summary>
public sealed class TemplateSubmissionFilter : PaginationRequest
{
    /// <summary>A <c>CustomTemplateStatus</c> name; null lists every status.</summary>
    public string? Status { get; set; }
    public Guid? DesignerUserId { get; set; }
}
