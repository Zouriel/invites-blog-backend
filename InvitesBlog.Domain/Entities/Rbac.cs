namespace InvitesBlog.Domain.Entities;

/// <summary>
/// An application account with roles. Admins get real accounts; inviters/invitees are
/// principals authenticated by possession token / OTP-JWT and mapped to built-in roles.
/// Introduced for the full-RBAC authorization model.
/// </summary>
public sealed class AppUser
{
    public Guid Id { get; set; }
    public string Email { get; set; } = default!;        // unique, lowercased
    public string DisplayName { get; set; } = default!;
    public string? PasswordHash { get; set; }            // admins only; null for token principals
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }

    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}

/// <summary>A named role that groups permissions (e.g. Admin, Inviter, Invitee).</summary>
public sealed class Role
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;         // unique
    public string Description { get; set; } = default!;
    public bool IsSystem { get; set; }                   // seeded, non-deletable

    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
    public ICollection<UserRole> UserRoles { get; set; } = new List<UserRole>();
}

/// <summary>A single permission string (e.g. "campaigns.write").</summary>
public sealed class Permission
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;         // unique, "entity.action"
    public string Group { get; set; } = default!;        // "campaigns", "templates", ...
    public string Description { get; set; } = default!;

    public ICollection<RolePermission> RolePermissions { get; set; } = new List<RolePermission>();
}

public sealed class RolePermission
{
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = default!;
    public Guid PermissionId { get; set; }
    public Permission Permission { get; set; } = default!;
}

public sealed class UserRole
{
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = default!;
    public Guid RoleId { get; set; }
    public Role Role { get; set; } = default!;
}
