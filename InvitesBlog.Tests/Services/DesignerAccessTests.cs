using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
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
        roles.FirstOrDefaultAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Role, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Designer);
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
}
