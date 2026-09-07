using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Admin;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Admin;
using InvitesBlog.Application.Filters.Admin;
using InvitesBlog.Application.Services.Admin;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class AdminServiceTests
{
    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly IRepository<Role> _roles = Substitute.For<IRepository<Role>>();
    private readonly IRepository<Permission> _permissions = Substitute.For<IRepository<Permission>>();
    private readonly ISuppressionRepository _suppression = Substitute.For<ISuppressionRepository>();
    private readonly IRepository<AuditLog> _auditLogs = Substitute.For<IRepository<AuditLog>>();
    private readonly IInviteeTokenIssuer _tokenIssuer = Substitute.For<IInviteeTokenIssuer>();
    private readonly IRepository<UserRole> _userRoles = Substitute.For<IRepository<UserRole>>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    private AdminService Sut() => new(
        _users, _roles, _permissions, _suppression, _auditLogs, _userRoles, _currentUser, _uow, _tokenIssuer);

    // ---------- granting and revoking a role ----------

    private readonly Guid _me = Guid.NewGuid();

    /// <summary>An account holding exactly the roles named, wired into the substitutes.</summary>
    private AppUser Account(params string[] roleNames)
    {
        var user = new AppUser { Id = Guid.NewGuid(), Email = "who@example.test", DisplayName = "Who" };
        foreach (var n in roleNames)
            user.UserRoles.Add(new UserRole
            {
                UserId = user.Id,
                RoleId = RoleRow(n).Id,
                Role = RoleRow(n),
            });
        _users.Query(Arg.Any<bool>()).Returns(new[] { user }.AsAsyncQueryable());
        return user;
    }

    private readonly Dictionary<string, Role> _roleRows = new();

    private Role RoleRow(string name)
    {
        if (_roleRows.TryGetValue(name, out var existing)) return existing;
        var row = new Role { Id = Guid.NewGuid(), Name = name, Description = name, IsSystem = true };
        _roleRows[name] = row;
        _roles.FirstOrDefaultAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<Role, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(c =>
            {
                var predicate = c.Arg<System.Linq.Expressions.Expression<Func<Role, bool>>>().Compile();
                return _roleRows.Values.FirstOrDefault(predicate);
            });
        return row;
    }

    [Fact]
    public async Task Granting_a_role_adds_it_and_records_why()
    {
        var user = Account(Roles.Customer);
        RoleRow(Roles.Subscriber);
        _currentUser.UserId.Returns(_me);

        var result = await Sut().SetUserRoleAsync(user.Id, new SetUserRoleRequest(Roles.Subscriber, true));

        Assert.Contains(Roles.Subscriber, result.Roles);
        await _auditLogs.Received().AddAsync(
            Arg.Is<AuditLog>(a => a.Action == "admin.role.grant"), Arg.Any<CancellationToken>());
        await _uow.Received().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The request says what should be TRUE afterwards, so a double-clicked toggle is not half an
    /// edit — and nothing is written, because nothing changed.
    /// </summary>
    [Fact]
    public async Task Granting_a_role_somebody_already_holds_changes_nothing()
    {
        var user = Account(Roles.Customer, Roles.Subscriber);
        _currentUser.UserId.Returns(_me);

        var result = await Sut().SetUserRoleAsync(user.Id, new SetUserRoleRequest(Roles.Subscriber, true));

        Assert.Equal(2, result.Roles.Count);
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Revoking_takes_the_role_away()
    {
        var user = Account(Roles.Customer, Roles.Subscriber);
        _currentUser.UserId.Returns(_me);

        var result = await Sut().SetUserRoleAsync(user.Id, new SetUserRoleRequest(Roles.Subscriber, false));

        Assert.DoesNotContain(Roles.Subscriber, result.Roles);
        await _auditLogs.Received().AddAsync(
            Arg.Is<AuditLog>(a => a.Action == "admin.role.revoke"), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Inviter, Invitee and Public say how a caller ARRIVED. A row granting one is read by nothing,
    /// so accepting it would look like it worked and do nothing at all.
    /// </summary>
    [Theory]
    [InlineData("Inviter")]
    [InlineData("Invitee")]
    [InlineData("Public")]
    [InlineData("Wizard")]
    public async Task A_role_nobody_can_hold_is_refused(string name)
    {
        var user = Account(Roles.Customer);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().SetUserRoleAsync(user.Id, new SetUserRoleRequest(name, true)));
        Assert.Equal("role_not_grantable", ex.ErrorCode);
    }

    [Fact]
    public async Task The_role_name_is_matched_without_regard_to_case()
    {
        var user = Account(Roles.Customer);
        RoleRow(Roles.Subscriber);
        _currentUser.UserId.Returns(_me);

        var result = await Sut().SetUserRoleAsync(user.Id, new SetUserRoleRequest("subscriber", true));

        Assert.Contains(Roles.Subscriber, result.Roles);
    }

    /// <summary>The likeliest way to lock everybody out is to try the toggle on your own row.</summary>
    [Fact]
    public async Task You_cannot_take_the_admin_role_off_yourself()
    {
        var user = Account(Roles.Admin, Roles.Customer);
        _currentUser.UserId.Returns(user.Id);
        _userRoles.CountAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<UserRole, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(5);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().SetUserRoleAsync(user.Id, new SetUserRoleRequest(Roles.Admin, false)));
        Assert.Equal("cannot_demote_self", ex.ErrorCode);
    }

    /// <summary>Same lock-out by a slower route. The seeder only restores the one configured address.</summary>
    [Fact]
    public async Task The_last_admin_cannot_be_demoted()
    {
        var user = Account(Roles.Admin, Roles.Customer);
        _currentUser.UserId.Returns(_me);
        _userRoles.CountAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<UserRole, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().SetUserRoleAsync(user.Id, new SetUserRoleRequest(Roles.Admin, false)));
        Assert.Equal("last_admin", ex.ErrorCode);
    }

    [Fact]
    public async Task A_second_admin_can_be_demoted_by_another_admin()
    {
        var user = Account(Roles.Admin, Roles.Customer);
        _currentUser.UserId.Returns(_me);
        _userRoles.CountAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<UserRole, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var result = await Sut().SetUserRoleAsync(user.Id, new SetUserRoleRequest(Roles.Admin, false));

        Assert.DoesNotContain(Roles.Admin, result.Roles);
    }

    /// <summary>Revoking a role that is not Admin never counts admins — nothing can be locked out.</summary>
    [Fact]
    public async Task Revoking_a_subscription_from_a_lone_admin_is_fine()
    {
        var user = Account(Roles.Admin, Roles.Subscriber);
        _currentUser.UserId.Returns(user.Id);
        _userRoles.CountAsync(
            Arg.Any<System.Linq.Expressions.Expression<Func<UserRole, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var result = await Sut().SetUserRoleAsync(user.Id, new SetUserRoleRequest(Roles.Subscriber, false));

        Assert.Contains(Roles.Admin, result.Roles);
        Assert.DoesNotContain(Roles.Subscriber, result.Roles);
    }

    [Fact]
    public async Task An_account_that_is_not_there_is_a_not_found()
    {
        Account(Roles.Customer);
        RoleRow(Roles.Subscriber);

        await Assert.ThrowsAsync<NotFoundException>(
            () => Sut().SetUserRoleAsync(Guid.NewGuid(), new SetUserRoleRequest(Roles.Subscriber, true)));
    }

    [Fact]
    public async Task ListPermissions_orders_by_group_then_name()
    {
        var p1 = new Permission { Id = Guid.NewGuid(), Name = "campaigns.write", Group = "campaigns", Description = "" };
        var p2 = new Permission { Id = Guid.NewGuid(), Name = "campaigns.read", Group = "campaigns", Description = "" };
        var p3 = new Permission { Id = Guid.NewGuid(), Name = "templates.read", Group = "templates", Description = "" };
        _permissions.Query().Returns(new[] { p3, p1, p2 }.AsAsyncQueryable());

        var list = await Sut().ListPermissionsAsync();

        Assert.Equal(new[] { "campaigns.read", "campaigns.write", "templates.read" }, list.Select(p => p.Name));
    }

    [Fact]
    public async Task ListAudit_applies_action_filter()
    {
        var a1 = new AuditLog { Id = Guid.NewGuid(), Action = "campaign.delete", CreatedAt = DateTimeOffset.UtcNow };
        var a2 = new AuditLog { Id = Guid.NewGuid(), Action = "guest.remove", CreatedAt = DateTimeOffset.UtcNow };
        _auditLogs.Query().Returns(new[] { a1, a2 }.AsAsyncQueryable());

        var page = await Sut().ListAuditAsync(new AuditLogFilter { Action = "campaign.delete" });

        Assert.Equal(1, page.TotalCount);
        Assert.Equal("campaign.delete", page.Items[0].Action);
    }
}
