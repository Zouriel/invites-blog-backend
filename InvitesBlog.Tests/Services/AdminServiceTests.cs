using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Admin;
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

    private AdminService Sut() => new(_users, _roles, _permissions, _suppression, _auditLogs, _tokenIssuer);

    [Fact]
    public async Task Login_wrong_password_throws()
    {
        var user = TestData.AdminUser(password: "correct-horse");
        _users.Query().Returns(new[] { user }.AsAsyncQueryable());

        await Assert.ThrowsAsync<AdminLoginFailedException>(
            () => Sut().LoginAsync(new AdminLoginRequest(user.Email, "wrong")));
    }

    [Fact]
    public async Task Login_unknown_user_throws()
    {
        _users.Query().Returns(Array.Empty<AppUser>().AsAsyncQueryable());
        await Assert.ThrowsAsync<AdminLoginFailedException>(
            () => Sut().LoginAsync(new AdminLoginRequest("nobody@test.com", "any")));
    }

    [Fact]
    public async Task Login_inactive_account_throws()
    {
        var user = TestData.AdminUser(isActive: false);
        _users.Query().Returns(new[] { user }.AsAsyncQueryable());
        await Assert.ThrowsAsync<AdminLoginFailedException>(
            () => Sut().LoginAsync(new AdminLoginRequest(user.Email, "correct-horse")));
    }

    [Fact]
    public async Task Login_non_admin_role_throws()
    {
        var user = TestData.AdminUser(isAdmin: false); // valid password, but not an Admin
        _users.Query().Returns(new[] { user }.AsAsyncQueryable());
        await Assert.ThrowsAsync<AdminLoginFailedException>(
            () => Sut().LoginAsync(new AdminLoginRequest(user.Email, "correct-horse")));
    }

    [Fact]
    public async Task Login_success_issues_admin_jwt()
    {
        var user = TestData.AdminUser(email: "admin@test.com", password: "correct-horse");
        _users.Query().Returns(new[] { user }.AsAsyncQueryable());
        _tokenIssuer.IssueForRole(Roles.Admin, Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<TimeSpan>())
            .Returns("admin-jwt");

        var res = await Sut().LoginAsync(new AdminLoginRequest("admin@test.com", "correct-horse"));

        Assert.Equal("admin-jwt", res.Token);
        Assert.Equal("admin@test.com", res.User.Email);
        Assert.Contains(Roles.Admin, res.User.Roles);
        _tokenIssuer.Received(1).IssueForRole(Roles.Admin, Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<TimeSpan>());
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
