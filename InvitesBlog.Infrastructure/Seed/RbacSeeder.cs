using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Seed;

/// <summary>
/// Seeds permissions, roles, role-permission assignments, and the initial admin account from the
/// static definitions (spec §Roles and Permission Seeders). Idempotent — safe to run every startup;
/// new permissions added in code are picked up here so endpoints keep permission coverage.
/// </summary>
public sealed class RbacSeeder(AppDbContext db, IConfiguration config, ILogger<RbacSeeder> logger)
{
    public async Task SeedAsync(CancellationToken ct = default)
    {
        await SeedPermissionsAsync(ct);
        await SeedRolesAsync(ct);
        await SeedAdminAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedPermissionsAsync(CancellationToken ct)
    {
        var existing = await db.Permissions.Select(p => p.Name).ToListAsync(ct);
        var existingSet = existing.ToHashSet();
        foreach (var (name, group, description) in Permissions.All)
        {
            if (existingSet.Contains(name)) continue;
            db.Permissions.Add(new Permission { Id = Guid.NewGuid(), Name = name, Group = group, Description = description });
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedRolesAsync(CancellationToken ct)
    {
        var permissions = await db.Permissions.ToDictionaryAsync(p => p.Name, ct);

        foreach (var (roleName, permissionNames) in Roles.Definitions)
        {
            var role = await db.Roles
                .Include(r => r.RolePermissions)
                .FirstOrDefaultAsync(r => r.Name == roleName, ct);

            if (role is null)
            {
                role = new Role { Id = Guid.NewGuid(), Name = roleName, Description = $"{roleName} role", IsSystem = true };
                db.Roles.Add(role);
            }

            var assigned = role.RolePermissions.Select(rp => rp.PermissionId).ToHashSet();
            foreach (var permName in permissionNames)
            {
                if (!permissions.TryGetValue(permName, out var perm) || assigned.Contains(perm.Id)) continue;
                role.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = perm.Id });
            }
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task SeedAdminAsync(CancellationToken ct)
    {
        var email = (config["Admin:Email"] ?? "admin@invites.blog").ToLowerInvariant();
        if (await db.Users.AnyAsync(u => u.Email == email, ct)) return;

        var adminRole = await db.Roles.FirstAsync(r => r.Name == Roles.Admin, ct);
        var password = config["Admin:Password"] ?? "ChangeMe!123";
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = "Administrator",
            PasswordHash = PasswordHasher.Hash(password),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UserRoles = { new UserRole { RoleId = adminRole.Id } }
        };
        db.Users.Add(user);
        logger.LogInformation("Seeded admin account {Email}.", email);
    }
}
