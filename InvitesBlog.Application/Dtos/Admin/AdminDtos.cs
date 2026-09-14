namespace InvitesBlog.Application.Dtos.Admin;

/// <summary>An application user with the names of the roles assigned to them.</summary>
public sealed record AdminUserDto(
    Guid Id, string? Email, string DisplayName, bool IsActive, IReadOnlyList<string> Roles,
    /// <summary>None, Basic or Premium, as set, whether or not it has ended.</summary>
    string SubscriptionTier = "None",
    DateTimeOffset? SubscriptionEndsAt = null,
    bool SubscriptionActive = false);

/// <summary>Sets an account's subscription. <c>None</c> ends it now.</summary>
public sealed record SetSubscriptionRequest(string Tier, DateTimeOffset? EndsAt);

/// <summary>An event an account organised, with its pass.</summary>
public sealed record AdminUserEventDto(
    Guid Id, string Title, DateTimeOffset EventStartAt, DateTimeOffset? EventPassUntil, bool PassActive);

/// <summary>Grants an event pass (adding six months) or takes it away.</summary>
public sealed record SetEventPassRequest(bool Granted);

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

/// <summary>Granting or taking away one role from one account.</summary>
/// <param name="Role">The role's NAME, which is what the seeder and every check use.</param>
/// <param name="Granted">True to give it, false to take it away.</param>
public sealed record SetUserRoleRequest(string Role, bool Granted);
