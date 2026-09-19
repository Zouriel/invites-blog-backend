using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Seed;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The permissions table follows the code both ways: a permission added in code is seeded, and one
/// taken out of code is deleted — with every role's hold on it — so the admin Permissions screen
/// never lists access that nothing checks and nobody can be granted.
/// </summary>
public class RbacSeederTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"rbac-{Guid.NewGuid()}")
            .Options);

    private static RbacSeeder Sut(AppDbContext db) =>
        new(db, new ConfigurationBuilder().Build(), NullLogger<RbacSeeder>.Instance);

    [Fact]
    public async Task A_permission_no_longer_in_code_is_deleted_with_its_role_grants()
    {
        using var db = NewDb();
        var ghost = new Permission { Id = Guid.NewGuid(), Name = "admin.access", Group = "admin", Description = "gone" };
        var role = new Role { Id = Guid.NewGuid(), Name = Roles.Admin, Description = "Admin role", IsSystem = true };
        db.Permissions.Add(ghost);
        db.Roles.Add(role);
        db.RolePermissions.Add(new RolePermission { RoleId = role.Id, PermissionId = ghost.Id });
        await db.SaveChangesAsync();

        await Sut(db).SeedAsync();

        Assert.False(await db.Permissions.AnyAsync(p => p.Name == "admin.access"));
        Assert.False(await db.RolePermissions.AnyAsync(rp => rp.PermissionId == ghost.Id));
        Assert.Equal(
            Permissions.All.Select(p => p.Name).OrderBy(n => n),
            (await db.Permissions.Select(p => p.Name).ToListAsync()).OrderBy(n => n));
    }

    [Fact]
    public async Task A_description_reworded_in_code_replaces_the_stored_one()
    {
        using var db = NewDb();
        var (name, group, description) = Permissions.All.First(p => p.Name == Permissions.Buckets.Read);
        var row = new Permission { Id = Guid.NewGuid(), Name = name, Group = "buckets", Description = "See your buckets" };
        db.Permissions.Add(row);
        await db.SaveChangesAsync();

        await Sut(db).SeedAsync();

        var after = await db.Permissions.SingleAsync(p => p.Name == name);
        Assert.Equal(row.Id, after.Id);
        Assert.Equal(description, after.Description);
        Assert.Equal(group, after.Group);
    }
}
