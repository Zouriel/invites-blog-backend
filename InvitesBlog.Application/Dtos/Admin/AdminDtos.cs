namespace InvitesBlog.Application.Dtos.Admin;

/// <summary>An application user with the names of the roles assigned to them.</summary>
public sealed record AdminUserDto(
    Guid Id, string Email, string DisplayName, bool IsActive, IReadOnlyList<string> Roles);

/// <summary>A role with the names of the permissions it grants.</summary>
public sealed record AdminRoleDto(
    Guid Id, string Name, string Description, bool IsSystem, IReadOnlyList<string> Permissions);

/// <summary>A single permission definition.</summary>
public sealed record AdminPermissionDto(Guid Id, string Name, string Group, string Description);

/// <summary>A hashed suppression-list entry (§15.3).</summary>
public sealed record SuppressionEntryDto(
    Guid Id, string ContactHash, string ContactType, DateTimeOffset CreatedAt);

/// <summary>An audit-log record (§15.6).</summary>
public sealed record AuditLogDto(
    Guid Id, string Action, string? Actor, Guid? CampaignId, string DataJson, DateTimeOffset CreatedAt);

/// <summary>Admin login credentials.</summary>
public sealed record AdminLoginRequest(string Email, string Password);

/// <summary>A successful admin login: the issued admin JWT plus the signed-in user.</summary>
public sealed record AdminLoginResultDto(string Token, DateTimeOffset ExpiresAt, AdminUserDto User);
