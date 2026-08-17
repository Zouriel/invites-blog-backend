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
