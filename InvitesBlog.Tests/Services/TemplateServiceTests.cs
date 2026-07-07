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
    private TemplateService Sut() => new(_templates);

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

    [Fact]
    public async Task GetCategories_returns_distinct_active_categories_sorted()
    {
        var a = TestData.Template(); a.Category = "wedding";
        var b = TestData.Template(); b.Category = "birthday";
        var c = TestData.Template(); c.Category = "wedding";
        _templates.Query().Returns(new[] { a, b, c }.AsAsyncQueryable());

        var cats = await Sut().GetCategoriesAsync();

        Assert.Equal(new[] { "birthday", "wedding" }, cats);
    }
}
