using InvitesBlog.Application.Common;

namespace InvitesBlog.Application.Filters.Admin;

/// <summary>Query filter for the admin user list (searchable by email / display name).</summary>
public sealed class AdminUserFilter : PaginationRequest
{
    public bool? IsActive { get; set; }
}

/// <summary>Query filter for the suppression list, optionally scoped to a contact type.</summary>
public sealed class SuppressionFilter : PaginationRequest
{
    public string? ContactType { get; set; }   // "phone" | "email"
}

/// <summary>Query filter for the audit log, optionally scoped to an action or campaign.</summary>
public sealed class AuditLogFilter : PaginationRequest
{
    public string? Action { get; set; }
    public Guid? CampaignId { get; set; }
}
