using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions.Templates;
using InvitesBlog.Application.Filters.Templates;
using InvitesBlog.Application.Services.Templates;
using InvitesBlog.Domain.Entities;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class TemplateServiceTests
{
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly InvitesBlog.Application.Abstractions.ICurrentUser _user = Substitute.For<InvitesBlog.Application.Abstractions.ICurrentUser>();
    private TemplateService Sut() => new(_templates, _user);

    [Fact]
    public async Task GetBySlug_unknown_throws_TemplateNotFound()
    {
        _templates.GetActiveBySlugAsync("missing", Arg.Any<CancellationToken>()).Returns((Template?)null);
        await Assert.ThrowsAsync<TemplateNotFoundException>(() => Sut().GetBySlugAsync("missing"));
    }

    [Fact]
    public async Task GetBySlug_success_returns_detail_dto()
    {
        var t = TestData.Template();
        _templates.GetActiveBySlugAsync(t.Slug, Arg.Any<CancellationToken>()).Returns(t);

        var dto = await Sut().GetBySlugAsync(t.Slug);

        Assert.Equal(t.Id, dto.Id);
        Assert.Equal(t.Name, dto.Name);
        Assert.Equal(t.PackageUrl, dto.PackageUrl);
        Assert.Equal(t.ManifestJson, dto.ManifestJson);
    }

    [Fact]
    public async Task A_private_template_made_for_someone_is_theirs_to_see_and_nobody_elses()
    {
        var t = TestData.Template();
        t.Visibility = TemplateVisibility.Private;
        t.DesignerUserId = Guid.NewGuid();
        t.AssignedEmail = "leena@example.com";
        _templates.GetActiveBySlugAsync(t.Slug, Arg.Any<CancellationToken>()).Returns(t);
        _templates.Query().Returns(new[] { t }.AsAsyncQueryable());

        _user.Contact.Returns("Leena@example.com");
        Assert.Equal(t.Id, (await Sut().GetBySlugAsync(t.Slug)).Id);
        Assert.Single(await Sut().GetDedicatedForAsync("leena@example.com"));

        _user.Contact.Returns("someone@example.com");
        await Assert.ThrowsAsync<TemplateNotFoundException>(() => Sut().GetBySlugAsync(t.Slug));
        Assert.Empty(await Sut().GetDedicatedForAsync("someone@example.com"));
    }

    /// <summary>
    /// The gallery lists Public templates only, so a design published Private used to be unreachable
    /// from event creation and its author had to publish it to the world to use their own work.
    /// </summary>
    [Fact]
    public async Task GetMine_returns_the_callers_own_templates_whatever_their_visibility()
    {
        var me = Guid.NewGuid();
        var minePrivate = TestData.Template();
        minePrivate.Visibility = TemplateVisibility.Private;
        minePrivate.DesignerUserId = me;
        var minePublic = TestData.Template();
        minePublic.Visibility = TemplateVisibility.Public;
        minePublic.DesignerUserId = me;
        var mineRetired = TestData.Template(active: false);
        mineRetired.DesignerUserId = me;
        var somebodyElses = TestData.Template();
        somebodyElses.DesignerUserId = Guid.NewGuid();
        _templates.Query().Returns(
            new[] { minePrivate, minePublic, mineRetired, somebodyElses }.AsAsyncQueryable());

        _user.UserId.Returns(me);
        var mine = await Sut().GetMineAsync();

        Assert.Equal(2, mine.Count);
        Assert.Contains(mine, t => t.Id == minePrivate.Id);
        Assert.Contains(mine, t => t.Id == minePublic.Id);
        Assert.DoesNotContain(mine, t => t.Id == mineRetired.Id);
        Assert.DoesNotContain(mine, t => t.Id == somebodyElses.Id);
    }

    [Fact]
    public async Task GetMine_is_empty_for_a_caller_with_no_account()
    {
        _templates.Query().Returns(new[] { TestData.Template() }.AsAsyncQueryable());
        _user.UserId.Returns((Guid?)null);

        Assert.Empty(await Sut().GetMineAsync());
    }

    [Fact]
    public async Task List_returns_only_active_and_applies_category_filter_and_paging()
    {
        var active1 = TestData.Template(); active1.Name = "Alpha"; active1.Category = "wedding";
        var active2 = TestData.Template(); active2.Name = "Beta"; active2.Category = "birthday";
        var inactive = TestData.Template(active: false); inactive.Name = "Zeta"; inactive.Category = "wedding";
        _templates.Query().Returns(new[] { active1, active2, inactive }.AsAsyncQueryable());

        var all = await Sut().ListAsync(new TemplateFilter());
        Assert.Equal(2, all.TotalCount); // inactive excluded

        var wedding = await Sut().ListAsync(new TemplateFilter { Category = "wedding" });
        Assert.Equal(1, wedding.TotalCount);
        Assert.Equal("Alpha", wedding.Items[0].Name);
    }

}
