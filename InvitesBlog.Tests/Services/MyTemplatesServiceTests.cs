using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The templates screen is one endpoint serving two audiences, so every rule here is about SCOPE:
/// what a designer may touch, and what must survive deletion because someone is already relying on it.
/// </summary>
public class MyTemplatesServiceTests
{
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly Guid _designerId = Guid.NewGuid();

    public MyTemplatesServiceTests()
    {
        _currentUser.UserId.Returns(_designerId);
        _campaigns.CountAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Campaign, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(0);
    }

    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly IInviterRepository _inviters = Substitute.For<IInviterRepository>();

    private MyTemplatesService Sut() =>
        new(_currentUser, _templates, _campaigns, _uow, _users, _inviters);

    private void AsAdmin() => _currentUser.HasPermission(Permissions.Templates.Manage).Returns(true);

    private Template Template(Guid? designerId = null) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Aurora Vows",
        Slug = "aurora-vows",
        Category = "Wedding",
        Version = "1.0.0",
        Description = "",
        PackageUrl = "https://cdn.test/aurora-vows/",
        IsActive = true,
        DesignerUserId = designerId,
    };

    private void Existing(Template t) =>
        _templates.Query(Arg.Any<bool>()).Returns(new[] { t }.AsAsyncQueryable());

    // ----- Scope -----

    [Fact]
    public async Task A_designer_cannot_touch_someone_elses_template()
    {
        var theirs = Template(designerId: Guid.NewGuid());
        Existing(theirs);

        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().DeleteAsync(theirs.Id));
    }

    [Fact]
    public async Task Everyone_lists_only_what_they_published_admins_included()
    {
        AsAdmin();
        var mine = Template(_designerId);
        var someoneElses = Template(designerId: Guid.NewGuid());
        var platform = Template();
        _templates.Query(Arg.Any<bool>()).Returns(new[] { mine, someoneElses, platform }.AsAsyncQueryable());
        _campaigns.Query(Arg.Any<bool>()).Returns(Array.Empty<Campaign>().AsAsyncQueryable());

        var page = await Sut().ListAsync();

        Assert.Equal([mine.Id], page.Templates.Select(t => t.Id));
    }

    // ----- Delete -----

    [Fact]
    public async Task A_template_in_use_is_unlisted_rather_than_deleted()
    {
        var t = Template(_designerId);
        Existing(t);
        _campaigns.CountAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Campaign, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(3);

        var result = await Sut().DeleteAsync(t.Id);

        Assert.False(result.Deleted);
        Assert.True(result.Unlisted);
        Assert.False(t.IsActive);
        _templates.DidNotReceive().Remove(Arg.Any<Template>());
    }

    /// <summary>
    /// A dedicated template made for one customer is promised to them — it's how they reach their
    /// invitation, even though no campaign exists yet.
    /// </summary>
    [Fact]
    public async Task A_template_made_for_a_customer_is_unlisted_rather_than_deleted()
    {
        AsAdmin();
        var t = Template();
        t.Visibility = TemplateVisibility.Dedicated;
        t.AssignedEmail = "a@test.com";
        Existing(t);

        var result = await Sut().DeleteAsync(t.Id);

        Assert.False(result.Deleted);
        Assert.True(result.Unlisted);
        _templates.DidNotReceive().Remove(Arg.Any<Template>());
    }

    [Fact]
    public async Task An_unused_template_is_deleted()
    {
        var t = Template(_designerId);
        Existing(t);

        var result = await Sut().DeleteAsync(t.Id);

        Assert.True(result.Deleted);
        _templates.Received(1).Remove(t);
    }

    // ----- The admin screen's delete -----

    /// <summary>
    /// The admin screen's delete used to be its own copy of this rule, and hard-deleted a template
    /// made for a customer that no campaign used yet — the very case "My designs" protects. It now
    /// calls the same method, so it unlists instead.
    /// </summary>
    [Fact]
    public async Task The_admin_delete_unlists_a_template_made_for_a_customer()
    {
        AsAdmin();
        var t = Template();
        t.Visibility = TemplateVisibility.Dedicated;
        t.AssignedEmail = "a@test.com";
        Existing(t);
        var controller = new InvitesBlog.Api.Controllers.AdminTemplatesController(_templates, _campaigns, Sut());

        var response = await controller.Delete(t.Id, CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(response);
        var body = Assert.IsType<InvitesBlog.Application.Common.ApiResponse<
            InvitesBlog.Api.Controllers.AdminTemplatesController.DeleteResultDto>>(ok.Value);
        Assert.False(body.Data!.Deleted);
        Assert.True(body.Data.Deactivated);
        Assert.False(t.IsActive);
        _templates.DidNotReceive().Remove(Arg.Any<Template>());
    }

    [Fact]
    public async Task The_admin_delete_still_hard_deletes_an_unused_template()
    {
        AsAdmin();
        var t = Template(designerId: Guid.NewGuid());
        Existing(t);
        var controller = new InvitesBlog.Api.Controllers.AdminTemplatesController(_templates, _campaigns, Sut());

        var response = await controller.Delete(t.Id, CancellationToken.None);

        var ok = Assert.IsType<Microsoft.AspNetCore.Mvc.OkObjectResult>(response);
        var body = Assert.IsType<InvitesBlog.Application.Common.ApiResponse<
            InvitesBlog.Api.Controllers.AdminTemplatesController.DeleteResultDto>>(ok.Value);
        Assert.True(body.Data!.Deleted);
        _templates.Received(1).Remove(t);
    }

    // ---------- who used a template, and when ----------

    private (Template Template, Campaign Event, AppUser Host) UsedBy(string visibility)
    {
        var t = Template(_designerId);
        t.Visibility = visibility;
        t.AssignedEmail = visibility == TemplateVisibility.Dedicated ? "client@x.mv" : null;
        _templates.Query(Arg.Any<bool>()).Returns(new[] { t }.AsAsyncQueryable());
        var host = new AppUser { Id = Guid.NewGuid(), Email = "client@x.mv", DisplayName = "Aisha" };
        var e = TestData.Campaign(templateId: t.Id);
        e.CreatedByUserId = host.Id;
        _campaigns.Query(Arg.Any<bool>()).Returns(new[] { e }.AsAsyncQueryable());
        _users.Query(Arg.Any<bool>()).Returns(new[] { host }.AsAsyncQueryable());
        _inviters.Query(Arg.Any<bool>()).Returns(Array.Empty<Inviter>().AsAsyncQueryable());
        return (t, e, host);
    }

    [Fact]
    public async Task A_design_made_for_a_client_shows_who_used_it_and_when()
    {
        var (t, e, host) = UsedBy(TemplateVisibility.Dedicated);
        var use = Assert.Single(await Sut().UsesAsync(t.Id));
        Assert.Equal(e.Id, use.CampaignId);
        Assert.Equal("Aisha", use.HostName);
        Assert.Equal(host.Email, use.HostEmail);
        Assert.Equal("Not finished", use.Status);
    }

    [Fact]
    public async Task A_stranger_on_a_public_design_is_not_named_except_to_an_admin()
    {
        var (t, _, _) = UsedBy(TemplateVisibility.Public);
        var use = Assert.Single(await Sut().UsesAsync(t.Id));
        Assert.Null(use.HostName);
        Assert.Null(use.HostEmail);

        AsAdmin();
        Assert.Equal("Aisha", Assert.Single(await Sut().UsesAsync(t.Id)).HostName);
    }

    [Fact]
    public async Task Another_designers_template_is_refused()
    {
        var t = Template(Guid.NewGuid());
        _templates.Query(Arg.Any<bool>()).Returns(new[] { t }.AsAsyncQueryable());
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().UsesAsync(t.Id));
    }
}
