using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Filters.Designers;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Approval is the only door into the gallery: it is where a Template row is created (or its version
/// bumped) and where a fresh manifest is generated — never before, and never by editing a live row in
/// place.
/// </summary>
public class TemplateReviewServiceTests
{
    private readonly IRepository<CustomTemplate> _submissions = Substitute.For<IRepository<CustomTemplate>>();
    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly ITemplatePackager _packager = Substitute.For<ITemplatePackager>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    public TemplateReviewServiceTests()
    {
        _users.Query(Arg.Any<bool>()).Returns(Array.Empty<AppUser>().AsAsyncQueryable());
        _packager.PublishAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => new TemplatePackage(
                $"https://cdn.test/{ci.ArgAt<string>(0)}/", "{\"slug\":\"published\"}",
                new TemplateStructure([], [], [], [])));
    }

    private TemplateReviewService Sut() => new(_submissions, _users, _templates, _packager, _uow);

    private CustomTemplate Submission(
        CustomTemplateStatus status = CustomTemplateStatus.Submitted,
        Guid? publishedTemplateId = null, string? requestedByEmail = null)
    {
        var entity = new CustomTemplate
        {
            Id = Guid.NewGuid(),
            DesignerUserId = Guid.NewGuid(),
            Name = "Aurora Vows",
            Slug = "aurora-vows-ab12cd",
            Category = "Wedding",
            Description = "A warm gold-on-ink invite.",
            Html = "<html><body><h1 data-var=\"event.title\"></h1></body></html>",
            PreviewImageUrl = "https://cdn.test/submissions/preview.png",
            Status = status,
            PublishedTemplateId = publishedTemplateId,
            RequestedByEmail = requestedByEmail,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        _submissions.Query(Arg.Any<bool>()).Returns(new[] { entity }.AsAsyncQueryable());
        return entity;
    }

    [Fact]
    public async Task Rejecting_without_a_reason_throws()
    {
        var entity = Submission();

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().ReviewAsync(entity.Id, new ReviewSubmissionRequest(false, "   ")));

        Assert.Equal("rejection_reason_required", ex.ErrorCode);
        await _templates.DidNotReceive().AddAsync(Arg.Any<Template>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rejecting_records_the_reason_and_publishes_nothing()
    {
        var entity = Submission();

        await Sut().ReviewAsync(entity.Id, new ReviewSubmissionRequest(false, " Uses a paid font "));

        Assert.Equal(CustomTemplateStatus.Rejected, entity.Status);
        Assert.Equal("Uses a paid font", entity.RejectionReason);
        await _packager.DidNotReceive().PublishAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Approving_a_new_submission_creates_the_gallery_template_at_version_one()
    {
        var entity = Submission();
        Template? created = null;
        await _templates.AddAsync(Arg.Do<Template>(t => created = t), Arg.Any<CancellationToken>());

        await Sut().ReviewAsync(entity.Id, new ReviewSubmissionRequest(true, null));

        Assert.Equal("1.0.0", created!.Version);
        Assert.Equal("aurora-vows-ab12cd", created.Slug);
        Assert.Equal(entity.DesignerUserId, created.DesignerUserId);
        // The designer's uploaded image is the card art — not a live page URL.
        Assert.Equal("https://cdn.test/submissions/preview.png", created.PreviewImageUrl);
        Assert.Equal(TemplateVisibility.Public, created.Visibility);
        Assert.Equal(CustomTemplateStatus.Published, entity.Status);
        Assert.Equal(created.Id, entity.PublishedTemplateId);
    }

    [Fact]
    public async Task Approving_a_commissioned_submission_starts_it_dedicated_to_the_requester()
    {
        var entity = Submission(requestedByEmail: "bride@example.com");
        Template? created = null;
        await _templates.AddAsync(Arg.Do<Template>(t => created = t), Arg.Any<CancellationToken>());

        await Sut().ReviewAsync(entity.Id, new ReviewSubmissionRequest(true, null));

        Assert.Equal(TemplateVisibility.Dedicated, created!.Visibility);
        Assert.Equal("bride@example.com", created.AssignedEmail);
    }

    [Fact]
    public async Task Approving_an_edit_bumps_the_existing_template_instead_of_creating_a_second_one()
    {
        var live = TestData.Template();
        live.Version = "1.0.4";
        live.Slug = "golden-bloom";
        _templates.GetByIdAsync(live.Id, Arg.Any<CancellationToken>()).Returns(live);
        var entity = Submission(publishedTemplateId: live.Id);

        await Sut().ReviewAsync(entity.Id, new ReviewSubmissionRequest(true, null));

        Assert.Equal("1.0.5", live.Version);
        Assert.Equal("Aurora Vows", live.Name);
        await _templates.DidNotReceive().AddAsync(Arg.Any<Template>(), Arg.Any<CancellationToken>());
        // The new version is published to its OWN path, so the old package stays byte-for-byte intact.
        await _packager.Received().PublishAsync(
            "templates/golden-bloom@1.0.5", "golden-bloom", "1.0.5", entity.Html, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task An_already_approved_submission_cannot_be_reviewed_again()
    {
        var entity = Submission(status: CustomTemplateStatus.Published);

        await Assert.ThrowsAsync<InvalidStateException>(
            () => Sut().ReviewAsync(entity.Id, new ReviewSubmissionRequest(true, null)));
    }

    [Fact]
    public async Task Listing_by_an_unknown_status_is_a_clear_error_not_an_empty_page()
    {
        Submission();

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().ListAsync(new TemplateSubmissionFilter { Status = "Wobbly" }));
    }

    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("2.7.9", "2.7.10")]
    [InlineData("nonsense", "1.0.1")]
    public void Version_bump_increments_the_patch_component(string current, string expected) =>
        Assert.Equal(expected, TemplateReviewService.NextVersion(current));
}
