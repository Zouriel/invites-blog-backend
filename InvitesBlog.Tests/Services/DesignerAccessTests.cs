using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>The template designer comes with Studio: on while the plan is in force, off after; admins always.</summary>
public class DesignerAccessTests
{
    private static readonly Role Designer = new() { Id = Guid.NewGuid(), Name = Roles.Designer };
    private static readonly Role Admin = new() { Id = Guid.NewGuid(), Name = Roles.Admin };

    private static AppUser User(SubscriptionTier tier, DateTimeOffset? ends, params Role[] roles)
    {
        var u = new AppUser { Id = Guid.NewGuid(), Email = "x@example.test", DisplayName = "X", SubscriptionTier = tier, SubscriptionEndsAt = ends };
        foreach (var r in roles) u.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = r.Id, Role = r });
        return u;
    }

    private static async Task<int> Sweep(params AppUser[] people)
    {
        var users = Substitute.For<IRepository<AppUser>>();
        users.Query(Arg.Any<bool>()).Returns(_ => people.AsAsyncQueryable());
        var roles = Substitute.For<IRepository<Role>>();
        roles.Query(Arg.Any<bool>()).Returns(_ => new[] { Designer, Admin }.AsAsyncQueryable());
        return await new DesignerAccessService(users, roles, Substitute.For<IUnitOfWork>()).SweepAsync();
    }

    private static bool Designs(AppUser u) => u.UserRoles.Any(ur => ur.RoleId == Designer.Id);

    [Fact]
    public async Task Studio_gives_the_designer_and_losing_it_takes_it_away()
    {
        var studio = User(SubscriptionTier.Studio, null);
        var ended = User(SubscriptionTier.Studio, DateTimeOffset.UtcNow.AddDays(-1), Designer);
        var none = User(SubscriptionTier.None, null, Designer);
        var admin = User(SubscriptionTier.None, null, Admin, Designer);

        var changed = await Sweep(studio, ended, none, admin);

        Assert.True(Designs(studio));
        Assert.False(Designs(ended));
        Assert.False(Designs(none));
        Assert.True(Designs(admin));
        Assert.Equal(3, changed);
    }

    /// <summary>
    /// Against a real DbContext: several accounts in one sweep, some already holding the role. The
    /// role row must be the tracked instance the users' roles share, or the save throws (as it did
    /// in production the first time).
    /// </summary>
    [Fact]
    public async Task A_sweep_over_accounts_that_already_hold_the_role_saves()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"designers-{Guid.NewGuid()}").Options);
        var designer = new Role { Id = Guid.NewGuid(), Name = Roles.Designer, Description = "d", IsSystem = true };
        db.Roles.Add(designer);
        AppUser Make(SubscriptionTier tier, bool holds)
        {
            var u = new AppUser { Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@x.test", DisplayName = "U", SubscriptionTier = tier, IsActive = true };
            if (holds) u.UserRoles.Add(new UserRole { UserId = u.Id, RoleId = designer.Id });
            db.Users.Add(u);
            return u;
        }
        var keeps = Make(SubscriptionTier.Studio, holds: true);
        var gains = Make(SubscriptionTier.Studio, holds: false);
        var loses = Make(SubscriptionTier.None, holds: true);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var sut = new DesignerAccessService(new BaseRepository<AppUser>(db), new BaseRepository<Role>(db), new UnitOfWork(db));
        Assert.Equal(2, await sut.SweepAsync());

        db.ChangeTracker.Clear();
        var holders = await db.UserRoles.Where(ur => ur.RoleId == designer.Id).Select(ur => ur.UserId).ToListAsync();
        Assert.Contains(keeps.Id, holders);
        Assert.Contains(gains.Id, holders);
        Assert.DoesNotContain(loses.Id, holders);
    }
}
