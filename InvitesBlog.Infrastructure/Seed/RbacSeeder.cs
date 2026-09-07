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
        await GrantDesignersCustomerRoleAsync(ct);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Everyone with an account is also a customer: one person can commission an invitation and
    /// design templates, and the unified sign-in shows both halves off the same roles. Existing
    /// accounts predate the Customer role, so they're granted it once here.
    /// </summary>
    private async Task GrantDesignersCustomerRoleAsync(CancellationToken ct)
    {
        var customer = await db.Roles.FirstOrDefaultAsync(r => r.Name == Roles.Customer, ct);
        if (customer is null) return;

        var missing = await db.Users
            .Where(u => !u.UserRoles.Any(ur => ur.RoleId == customer.Id))
            .ToListAsync(ct);

        foreach (var user in missing)
            db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = customer.Id });

        if (missing.Count > 0)
            logger.LogInformation("Granted the Customer role to {Count} existing account(s).", missing.Count);
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

    /// <summary>
    /// Makes sure the configured address holds the Admin role — creating the account if there isn't
    /// one, and GRANTING the role if there is.
    ///
    /// <para><b>The grant half is the point.</b> This used to return the moment the address already
    /// existed, so pointing <c>Admin:Email</c> at a real person's account did nothing at all: they
    /// had signed up as an ordinary customer long before anyone thought to name them here, the
    /// account existed, and the seeder skipped straight past it. Silently — which is the worst way
    /// for an authorization change to fail.</para>
    ///
    /// <para>Written as ensure-rather-than-create so it is safe on every startup and cannot lock
    /// anybody out: an admin who loses the role gets it back on the next deploy.</para>
    /// </summary>
    private async Task SeedAdminAsync(CancellationToken ct)
    {
        var email = (config["Admin:Email"] ?? "admin@invites.blog").ToLowerInvariant();
        var adminRole = await db.Roles.FirstAsync(r => r.Name == Roles.Admin, ct);

        var user = await db.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(u => u.Email == email, ct);

        if (user is null)
        {
            var password = config["Admin:Password"] ?? "ChangeMe!123";
            user = new AppUser
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
            return;
        }

        if (user.UserRoles.Any(ur => ur.RoleId == adminRole.Id)) return;

        db.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = adminRole.Id });
        logger.LogInformation("Granted the Admin role to the existing account {Email}.", email);
    }
}
