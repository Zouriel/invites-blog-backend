using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Designs;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.Designs;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Repositories;
using InvitesBlog.Infrastructure.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Publishing from the designer goes live without review, so the rules standing in for a reviewer are
/// what these pin down: the compile happens on the server from the stored scene, a stale tab can't
/// publish over newer work, the gallery needs a description and has a daily limit, and an account or
/// template an admin acted on stays out of the gallery.
/// </summary>
public class DesignServiceTests
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"designs-{Guid.NewGuid()}").Options);

    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly ITemplatePackager _packager = Substitute.For<ITemplatePackager>();
    private readonly IStorageService _storage = Substitute.For<IStorageService>();
    private readonly ICampaignOwnershipService _ownership = Substitute.For<ICampaignOwnershipService>();
    private readonly ICampaignService _campaignService = Substitute.For<ICampaignService>();
    private readonly Guid _me = Guid.NewGuid();
    private string? _publishedHtml;

    public DesignServiceTests()
    {
        _currentUser.UserId.Returns(_me);
        _currentUser.IsAuthenticated.Returns(true);
        _packager.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>(), Arg.Any<byte[]?>())
            .Returns(ci =>
            {
                _publishedHtml = ci.ArgAt<string>(3);
                return new TemplatePackage($"/assets/{ci.ArgAt<string>(0)}/", "{}", new TemplateStructure([], [], [], []));
            });
        _storage.PutAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => "/assets/" + ci.ArgAt<string>(0));

        _db.Users.Add(new AppUser { Id = _me, Email = "maker@example.com", DisplayName = "Maker", IsActive = true });
        _db.TemplateTypes.Add(new TemplateType { Id = Guid.NewGuid(), Name = "Wedding", Slug = "wedding", IsActive = true });
        _db.TemplateTypes.Add(new TemplateType { Id = Guid.NewGuid(), Name = "Birthday", Slug = "birthday", IsActive = false });
        _db.SaveChanges();
    }

    private DesignService Sut()
    {
        var engine = new DesignEngine(
            new RawTemplatePackager(Substitute.For<IStorageService>()),
            Substitute.For<IImageOptimizer>(),
            new ConfigurationBuilder().Build());
        return new DesignService(
            _currentUser,
            new BaseRepository<TemplateDesign>(_db),
            new BaseRepository<TemplateDesignPublish>(_db),
            new BaseRepository<AppUser>(_db),
            new BaseRepository<TemplateType>(_db),
            new TemplateRepository(_db),
            new CampaignRepository(_db),
            _ownership,
            _campaignService,
            engine,
            _packager,
            _storage,
            new UnitOfWork(_db));
    }

    private static PublishDesignRequest Publish(int revision, string visibility = "Private", string? description = null,
        string category = "Wedding", byte[]? poster = null) =>
        new(visibility, "Ivory evening", category, description, null, revision, poster, poster is null ? null : "image/png");

    private async Task<DesignDto> NewDesignAsync(DesignService sut) =>
        await sut.CreateAsync(new CreateDesignRequest(null, "wedding", null, null, null));

    [Fact]
    public async Task A_private_publish_creates_a_private_template_in_the_chosen_type()
    {
        var sut = Sut();
        var design = await NewDesignAsync(sut);

        var result = await sut.PublishAsync(design.Id, Publish(design.Revision));

        var template = await _db.Templates.SingleAsync();
        Assert.Equal(TemplateVisibility.Private, template.Visibility);
        Assert.Equal("Wedding", template.Category);
        Assert.Equal("1.0.0", template.Version);
        Assert.Equal(_me, template.DesignerUserId);
        Assert.Equal(template.Id, result.TemplateId);
        Assert.Contains("animation-timeline", _publishedHtml);
    }

    [Fact]
    public async Task Publishing_again_bumps_the_version_of_the_same_template()
    {
        var sut = Sut();
        var design = await NewDesignAsync(sut);
        var first = await sut.PublishAsync(design.Id, Publish(design.Revision));

        var second = await sut.PublishAsync(design.Id, Publish(design.Revision));

        Assert.Equal(first.Slug, second.Slug);
        Assert.Equal("1.0.1", second.Version);
        Assert.Equal(1, await _db.Templates.CountAsync());
    }

    [Fact]
    public async Task A_stale_revision_is_refused()
    {
        var sut = Sut();
        var design = await NewDesignAsync(sut);

        await Assert.ThrowsAsync<InvalidStateException>(() => sut.PublishAsync(design.Id, Publish(design.Revision - 1)));
        Assert.Empty(_db.Templates);
    }

    [Fact]
    public async Task An_inactive_or_unknown_template_type_is_refused()
    {
        var sut = Sut();
        var design = await NewDesignAsync(sut);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => sut.PublishAsync(design.Id, Publish(design.Revision, category: "Birthday")));
        Assert.Equal("category_required", ex.ErrorCode);
    }

    [Fact]
    public async Task The_gallery_needs_a_description()
    {
        var sut = Sut();
        var design = await NewDesignAsync(sut);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => sut.PublishAsync(design.Id, Publish(design.Revision, "Public", "short")));
        Assert.Equal("description_required", ex.ErrorCode);
    }

    [Fact]
    public async Task Gallery_publishes_are_limited_per_day_but_private_ones_are_not()
    {
        var sut = Sut();
        var design = await NewDesignAsync(sut);
        for (var i = 0; i < DesignService.PublicPublishesPerDay; i++)
            _db.Add(new TemplateDesignPublish
            {
                Id = Guid.NewGuid(), DesignId = Guid.NewGuid(), UserId = _me, TemplateId = Guid.NewGuid(), Version = "1.0.0",
                Visibility = TemplateVisibility.Public, Revision = 1, PublishedAt = DateTimeOffset.UtcNow.AddHours(-1),
            });
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.PublishAsync(design.Id, Publish(design.Revision, "Public", "Ivory and gold with a pinned cover photo.")));
        await sut.PublishAsync(design.Id, Publish(design.Revision, "Private"));
    }

    [Fact]
    public async Task An_account_whose_template_was_removed_can_only_publish_privately()
    {
        (await _db.Users.SingleAsync(u => u.Id == _me)).PublicPublishingRevokedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();
        var sut = Sut();
        var design = await NewDesignAsync(sut);

        await Assert.ThrowsAsync<ForbiddenException>(() =>
            sut.PublishAsync(design.Id, Publish(design.Revision, "Public", "Ivory and gold with a pinned cover photo.")));
        var result = await sut.PublishAsync(design.Id, Publish(design.Revision, "Private"));
        Assert.Equal(TemplateVisibility.Private, result.Visibility);
    }

    [Fact]
    public async Task A_template_unlisted_by_an_admin_cant_be_put_back_in_the_gallery()
    {
        var sut = Sut();
        var design = await NewDesignAsync(sut);
        await sut.PublishAsync(design.Id, Publish(design.Revision, "Public", "Ivory and gold with a pinned cover photo."));
        var template = await _db.Templates.SingleAsync();
        template.Visibility = TemplateVisibility.Private;
        template.UnlistedByAdminAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<ForbiddenException>(() => sut.SetVisibilityAsync(design.Id, new SetDesignVisibilityRequest("Public")));
    }

    [Fact]
    public async Task A_poster_that_isnt_an_image_is_refused()
    {
        var sut = Sut();
        var design = await NewDesignAsync(sut);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            sut.PublishAsync(design.Id, Publish(design.Revision, poster: "<svg onload=alert(1)>"u8.ToArray())));
        Assert.Equal("poster_invalid", ex.ErrorCode);
    }

    [Fact]
    public async Task Someone_elses_design_is_not_found()
    {
        var sut = Sut();
        var design = await NewDesignAsync(sut);
        _currentUser.UserId.Returns(Guid.NewGuid());

        await Assert.ThrowsAsync<NotFoundException>(() => sut.GetAsync(design.Id));
        await Assert.ThrowsAsync<NotFoundException>(() => sut.PublishAsync(design.Id, Publish(design.Revision)));
    }

    [Fact]
    public async Task Someone_elses_template_cant_be_opened_for_editing()
    {
        var template = HandWritten(designer: Guid.NewGuid());
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        await Assert.ThrowsAsync<NotFoundException>(() =>
            Sut().CreateAsync(new CreateDesignRequest(null, null, template.Id, null, null)));
    }

    [Fact]
    public async Task A_hand_written_template_has_to_be_converted_first()
    {
        var template = HandWritten(designer: _me);
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Sut().CreateAsync(new CreateDesignRequest(null, null, template.Id, null, null)));
        Assert.Equal("import_required", ex.ErrorCode);
    }

    [Fact]
    public async Task A_designer_made_template_reopens_as_its_scene_and_publishes_over_itself()
    {
        var sut = Sut();
        var design = await NewDesignAsync(sut);
        var published = await sut.PublishAsync(design.Id, Publish(design.Revision));
        await sut.DeleteAsync(design.Id);

        var reopened = await sut.CreateAsync(new CreateDesignRequest(null, null, published.TemplateId, null, null));
        var again = await sut.PublishAsync(reopened.Id, Publish(reopened.Revision));

        Assert.Equal(published.TemplateId, again.TemplateId);
        Assert.Equal("1.0.1", again.Version);
        Assert.Equal(
            JsonSerializer.Serialize(design.Scene.GetProperty("elements")),
            JsonSerializer.Serialize(reopened.Scene.GetProperty("elements")));
    }

    [Fact]
    public async Task An_admin_can_open_any_template_but_a_second_open_resumes_the_same_design()
    {
        var template = HandWritten(designer: Guid.NewGuid());
        _db.Templates.Add(template);
        await _db.SaveChangesAsync();
        _currentUser.HasPermission(Permissions.Templates.Manage).Returns(true);
        var sut = Sut();
        var scene = (await NewDesignAsync(sut)).Scene;

        var first = await sut.CreateAsync(new CreateDesignRequest(null, null, template.Id, scene, null));
        var second = await sut.CreateAsync(new CreateDesignRequest(null, null, template.Id, scene, null));

        Assert.Equal(first.Id, second.Id);
    }

    private static Template HandWritten(Guid designer) => new()
    {
        Id = Guid.NewGuid(), Name = "Gilded Hour", Slug = $"gilded-{Guid.NewGuid():N}"[..20], Version = "1.0.0",
        Category = "Wedding", Description = "d", PreviewImageUrl = "p", SceneJson = "{}", ManifestJson = "{}",
        PackageUrl = "/assets/x/", IsActive = true, Visibility = TemplateVisibility.Public, DesignerUserId = designer,
    };
}
