using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Dtos.Designs;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Designs;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>Reports are how a live-immediately gallery gets moderated after the fact.</summary>
public class TemplateReportServiceTests
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"reports-{Guid.NewGuid()}").Options);
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly Guid _ownerId = Guid.NewGuid();
    private readonly Template _template;

    public TemplateReportServiceTests()
    {
        _db.Users.Add(new AppUser { Id = _ownerId, Email = "maker@example.com", DisplayName = "Maker", IsActive = true });
        _template = new Template
        {
            Id = Guid.NewGuid(), Name = "Ivory", Slug = "ivory", Version = "1.0.0", Category = "Wedding", Description = "d",
            PreviewImageUrl = "p", SceneJson = "{}", ManifestJson = "{}", PackageUrl = "/assets/x/", IsActive = true,
            Visibility = TemplateVisibility.Public, DesignerUserId = _ownerId,
        };
        _db.Templates.Add(_template);
        _db.SaveChanges();
    }

    private TemplateReportService Sut() => new(
        _currentUser, new BaseRepository<TemplateReport>(_db), new BaseRepository<AppUser>(_db), new TemplateRepository(_db), new UnitOfWork(_db));

    private async Task ReportAsUserAsync(Guid? user, string reason = "spam")
    {
        _currentUser.UserId.Returns(user);
        await Sut().ReportAsync(_template.Id, new ReportTemplateRequest(reason, "details"));
    }

    [Fact]
    public async Task Only_gallery_templates_can_be_reported()
    {
        _template.Visibility = TemplateVisibility.Private;
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() => ReportAsUserAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Reporting_again_updates_the_open_report()
    {
        var reporter = Guid.NewGuid();
        await ReportAsUserAsync(reporter, "spam");
        await ReportAsUserAsync(reporter, "offensive");

        var report = await _db.Set<TemplateReport>().SingleAsync();
        Assert.Equal("offensive", report.Reason);
    }

    [Fact]
    public async Task Dismissing_settles_only_that_report()
    {
        await ReportAsUserAsync(Guid.NewGuid());
        await ReportAsUserAsync(Guid.NewGuid());
        var first = await _db.Set<TemplateReport>().AsNoTracking().OrderBy(r => r.CreatedAt).FirstAsync();

        await Sut().ResolveAsync(first.Id, new ResolveReportRequest("dismiss", null));

        Assert.Equal(1, await _db.Set<TemplateReport>().CountAsync(r => r.Status == TemplateReportStatus.Open));
        Assert.Equal(TemplateVisibility.Public, (await _db.Templates.AsNoTracking().SingleAsync()).Visibility);
    }

    [Fact]
    public async Task Removing_takes_it_out_of_the_gallery_settles_every_report_and_revokes_the_maker()
    {
        await ReportAsUserAsync(Guid.NewGuid());
        await ReportAsUserAsync(Guid.NewGuid());
        var first = await _db.Set<TemplateReport>().AsNoTracking().FirstAsync();

        await Sut().ResolveAsync(first.Id, new ResolveReportRequest("remove", "copied"));

        var template = await _db.Templates.AsNoTracking().SingleAsync();
        Assert.Equal(TemplateVisibility.Private, template.Visibility);
        Assert.False(template.IsActive);
        Assert.NotNull(template.UnlistedByAdminAt);
        Assert.Equal(0, await _db.Set<TemplateReport>().CountAsync(r => r.Status == TemplateReportStatus.Open));
        Assert.NotNull((await _db.Users.AsNoTracking().SingleAsync(u => u.Id == _ownerId)).PublicPublishingRevokedAt);
    }

    [Fact]
    public async Task Unlisting_keeps_the_template_usable_and_the_maker_able_to_publish()
    {
        await ReportAsUserAsync(Guid.NewGuid());
        var report = await _db.Set<TemplateReport>().AsNoTracking().SingleAsync();

        await Sut().ResolveAsync(report.Id, new ResolveReportRequest("unlist", null));

        var template = await _db.Templates.AsNoTracking().SingleAsync();
        Assert.Equal(TemplateVisibility.Private, template.Visibility);
        Assert.True(template.IsActive);
        Assert.Null((await _db.Users.AsNoTracking().SingleAsync(u => u.Id == _ownerId)).PublicPublishingRevokedAt);
    }

    [Fact]
    public async Task An_admin_unpublishes_a_gallery_template_and_can_put_it_back()
    {
        await Sut().UnpublishAsync(_template.Id);
        var unpublished = await _db.Templates.AsNoTracking().SingleAsync();
        Assert.Equal(TemplateVisibility.Private, unpublished.Visibility);
        Assert.NotNull(unpublished.UnlistedByAdminAt); // the creator can't list it again themselves
        Assert.True(unpublished.IsActive);              // still theirs to use

        await Sut().RepublishAsync(_template.Id);
        var back = await _db.Templates.AsNoTracking().SingleAsync();
        Assert.Equal(TemplateVisibility.Public, back.Visibility);
        Assert.Null(back.UnlistedByAdminAt);
    }

    [Fact]
    public async Task Only_a_gallery_template_can_be_unpublished()
    {
        await Sut().UnpublishAsync(_template.Id);
        await Assert.ThrowsAsync<BusinessRuleException>(() => Sut().UnpublishAsync(_template.Id));
    }
}
