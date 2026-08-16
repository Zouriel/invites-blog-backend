using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The scan runs BEFORE a row exists, so a rejected upload never reaches the review queue — and the
/// submission is staged outside the live templates/ path, so nothing a designer uploads is servable
/// as a gallery template until an admin approves it.
/// </summary>
public class DesignerTemplateServiceTests
{
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IRepository<CustomTemplate> _submissions = Substitute.For<IRepository<CustomTemplate>>();
    private readonly IRepository<Inquiry> _inquiries = Substitute.For<IRepository<Inquiry>>();
    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly ITemplatePackager _packager = Substitute.For<ITemplatePackager>();
    private readonly IStorageService _storage = Substitute.For<IStorageService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly Guid _designerId = Guid.NewGuid();

    public DesignerTemplateServiceTests()
    {
        _currentUser.UserId.Returns(_designerId);
        _packager.RecommendedBytes.Returns(300 * 1024);
        _packager.MaxBytes.Returns(800 * 1024);
        _packager.Describe(Arg.Any<string>(), Arg.Any<string>())
            .Returns(new TemplateStructure(["Title (text)"], ["Cover photo"], ["bride"], ["accentColor"]));
        _packager.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TemplatePackage(
                $"https://cdn.test/{ci.ArgAt<string>(0)}/", "{}", new TemplateStructure([], [], [], [])));
        _storage.PutAsync(Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"https://cdn.test/{ci.ArgAt<string>(0)}");
        _templates.Query(Arg.Any<bool>()).Returns(Array.Empty<Template>().AsAsyncQueryable());
        _users.GetByIdAsync(_designerId, Arg.Any<CancellationToken>())
            .Returns(new AppUser { Id = _designerId, Email = "d@test.com", DisplayName = "D", IsActive = true });
    }

    private DesignerTemplateService Sut() =>
        new(_currentUser, _submissions, _inquiries, _users, _templates, _packager, _storage, _uow);

    private static SubmitTemplateRequest Request(string html = "<html><body></body></html>") =>
        new("Aurora Vows", "Wedding", "A warm invite.", html,
            new UploadedFile("preview.png", "image/png", [1, 2, 3]));

    [Fact]
    public async Task A_template_that_fails_the_scan_never_becomes_a_submission()
    {
        _packager.When(p => p.Scan(Arg.Any<string>()))
            .Do(_ => throw new BusinessRuleException("No scripts.", "template_script_not_allowed"));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sut().SubmitAsync(Request()));

        Assert.Equal("template_script_not_allowed", ex.ErrorCode);
        await _submissions.DidNotReceive().AddAsync(Arg.Any<CustomTemplate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Submitting_without_a_preview_image_is_rejected()
    {
        var request = Request() with { PreviewImage = new UploadedFile("", "", []) };

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sut().SubmitAsync(request));

        Assert.Equal("template_preview_required", ex.ErrorCode);
    }

    [Fact]
    public async Task A_preview_that_is_not_an_image_is_rejected()
    {
        var request = Request() with { PreviewImage = new UploadedFile("notes.pdf", "application/pdf", [1]) };

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sut().SubmitAsync(request));

        Assert.Equal("template_preview_not_an_image", ex.ErrorCode);
    }

    [Fact]
    public async Task A_submission_is_staged_outside_the_live_templates_path()
    {
        CustomTemplate? added = null;
        await _submissions.AddAsync(Arg.Do<CustomTemplate>(t => added = t), Arg.Any<CancellationToken>());

        var dto = await Sut().SubmitAsync(Request());

        Assert.Equal(nameof(CustomTemplateStatus.Submitted), dto.Status);
        Assert.Equal(_designerId, added!.DesignerUserId);
        await _packager.Received().PublishAsync(
            $"submissions/{added.Id}", added.Slug, "review", Arg.Any<string>(), Arg.Any<CancellationToken>());
        Assert.DoesNotContain("templates/", added.PackageUrl);
    }

    [Fact]
    public async Task The_slug_is_generated_from_the_name_and_never_collides()
    {
        var slugs = new List<string>();
        await _submissions.AddAsync(Arg.Do<CustomTemplate>(t => slugs.Add(t.Slug)), Arg.Any<CancellationToken>());

        await Sut().SubmitAsync(Request());
        await Sut().SubmitAsync(Request());

        Assert.All(slugs, s => Assert.StartsWith("aurora-vows-", s));
        Assert.Equal(2, slugs.Distinct().Count());
    }

    [Fact]
    public async Task The_raw_source_is_kept_for_audit_and_re_review()
    {
        CustomTemplate? added = null;
        await _submissions.AddAsync(Arg.Do<CustomTemplate>(t => added = t), Arg.Any<CancellationToken>());

        await Sut().SubmitAsync(Request("<html><body><h1>hi</h1></body></html>"));

        Assert.Equal("<html><body><h1>hi</h1></body></html>", added!.Html);
    }

    [Fact]
    public async Task An_approved_submission_can_no_longer_be_edited_by_its_designer()
    {
        var entity = new CustomTemplate
        {
            Id = Guid.NewGuid(),
            DesignerUserId = _designerId,
            Status = CustomTemplateStatus.Published,
            Slug = "aurora-vows-ab12cd",
            Html = ""
        };
        _submissions.Query(Arg.Any<bool>()).Returns(new[] { entity }.AsAsyncQueryable());

        var ex = await Assert.ThrowsAsync<InvalidStateException>(
            () => Sut().ResubmitAsync(entity.Id, Request()));

        Assert.Equal("submission_not_revisable", ex.ErrorCode);
    }

    [Fact]
    public async Task Another_designers_submission_is_not_found()
    {
        var entity = new CustomTemplate { Id = Guid.NewGuid(), DesignerUserId = Guid.NewGuid(), Html = "" };
        _submissions.Query(Arg.Any<bool>()).Returns(new[] { entity }.AsAsyncQueryable());

        await Assert.ThrowsAsync<NotFoundException>(() => Sut().GetMineAsync(entity.Id));
    }

    [Fact]
    public async Task Resubmitting_a_rejected_template_clears_the_rejection_and_requeues_it()
    {
        var entity = new CustomTemplate
        {
            Id = Guid.NewGuid(),
            DesignerUserId = _designerId,
            Status = CustomTemplateStatus.Rejected,
            RejectionReason = "Uses a paid font",
            Slug = "aurora-vows-ab12cd",
            Html = ""
        };
        _submissions.Query(Arg.Any<bool>()).Returns(new[] { entity }.AsAsyncQueryable());

        var dto = await Sut().ResubmitAsync(entity.Id, Request());

        Assert.Equal(nameof(CustomTemplateStatus.Submitted), dto.Status);
        Assert.Null(dto.RejectionReason);
    }

    [Fact]
    public async Task Release_state_comes_from_the_published_template_not_the_stale_submission_row()
    {
        // Consent is recorded on the Template when either party agrees, so the submission row's own
        // flags go stale the moment that happens — the designer's dashboard must not show them.
        var live = TestData.Template();
        live.Visibility = TemplateVisibility.Dedicated;
        live.DesignerConsentToPublish = true;
        live.RequesterConsentToPublish = true;
        live.RequestedByEmail = "aisha@example.com";
        _templates.Query(Arg.Any<bool>()).Returns(new[] { live }.AsAsyncQueryable());

        var submission = new CustomTemplate
        {
            Id = Guid.NewGuid(),
            DesignerUserId = _designerId,
            Status = CustomTemplateStatus.Published,
            PublishedTemplateId = live.Id,
            Slug = "s",
            Html = "",
            DesignerConsentToPublish = false,   // stale
            RequesterConsentToPublish = false,  // stale
        };
        _submissions.Query(Arg.Any<bool>()).Returns(new[] { submission }.AsAsyncQueryable());

        var dto = Assert.Single(await Sut().ListMineAsync());

        Assert.True(dto.DesignerConsentToPublish);
        Assert.True(dto.RequesterConsentToPublish);
        Assert.Equal(TemplateVisibility.Dedicated, dto.PublishedVisibility);
        Assert.Equal("aisha@example.com", dto.RequestedByEmail);
    }

    [Fact]
    public async Task A_suspended_designer_cannot_submit_even_with_a_token_issued_before_suspension()
    {
        // Their JWT stays cryptographically valid for its full lifetime, so suspension has to be
        // enforced per request — otherwise "suspend" wouldn't actually stop anything for days.
        _users.GetByIdAsync(_designerId, Arg.Any<CancellationToken>())
            .Returns(new AppUser { Id = _designerId, Email = "d@test.com", DisplayName = "D", IsActive = false });

        await Assert.ThrowsAsync<InvitesBlog.Application.Exceptions.Designers.DesignerSuspendedException>(
            () => Sut().SubmitAsync(Request()));

        await _submissions.DidNotReceive().AddAsync(Arg.Any<CustomTemplate>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void Scan_reports_the_failure_instead_of_throwing_so_the_form_can_show_it()
    {
        _packager.When(p => p.Scan(Arg.Any<string>()))
            .Do(_ => throw new BusinessRuleException("No scripts.", "template_script_not_allowed"));

        var result = Sut().Scan("<script>alert(1)</script>");

        Assert.False(result.Passed);
        Assert.Equal("template_script_not_allowed", result.ErrorCode);
    }

    [Fact]
    public void Scan_reports_what_the_template_declares_and_flags_the_soft_budget()
    {
        var result = Sut().Scan(new string('x', 400 * 1024));

        Assert.True(result.Passed);
        Assert.True(result.OverRecommendedBudget);   // over 300KB but under the 800KB hard limit
        Assert.Equal(["Title (text)"], result.Fields);
        Assert.Equal(["bride"], result.Roles);
        Assert.Equal(["accentColor"], result.ThemeKeys);
    }
}
