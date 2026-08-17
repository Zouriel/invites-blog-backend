using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The templates screen is one endpoint serving two audiences, so every rule here is about SCOPE:
/// what a designer may touch, what only an admin may set, and what must survive deletion because
/// someone is already relying on it.
/// </summary>
public class MyTemplatesServiceTests
{
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly IRepository<CustomTemplate> _submissions = Substitute.For<IRepository<CustomTemplate>>();
    private readonly IRepository<Inquiry> _inquiries = Substitute.For<IRepository<Inquiry>>();
    private readonly IStorageService _storage = Substitute.For<IStorageService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly Guid _designerId = Guid.NewGuid();

    public MyTemplatesServiceTests()
    {
        _currentUser.UserId.Returns(_designerId);
        _submissions.Query(Arg.Any<bool>()).Returns(Array.Empty<CustomTemplate>().AsAsyncQueryable());
        _inquiries.Query(Arg.Any<bool>()).Returns(Array.Empty<Inquiry>().AsAsyncQueryable());
        _campaigns.CountAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Campaign, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(0);
    }

    private MyTemplatesService Sut() =>
        new(_currentUser, _templates, _campaigns, _submissions, _inquiries, _storage, _uow);

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
    public async Task A_designer_cannot_set_the_commission_the_platform_pays()
    {
        var mine = Template(_designerId);
        mine.CommissionPrice = 1500m;
        Existing(mine);

        await Sut().SetPricingAsync(mine.Id, new SetTemplatePricingRequest(UsagePrice: 250m, CommissionPrice: 9999m));

        Assert.Equal(250m, mine.UsagePrice);      // their own per-use fee, theirs to set
        Assert.Equal(1500m, mine.CommissionPrice); // untouched
    }

    [Fact]
    public async Task An_admin_sets_both_prices()
    {
        AsAdmin();
        var t = Template(_designerId);
        Existing(t);

        await Sut().SetPricingAsync(t.Id, new SetTemplatePricingRequest(UsagePrice: 250m, CommissionPrice: 1500m));

        Assert.Equal(250m, t.UsagePrice);
        Assert.Equal(1500m, t.CommissionPrice);
    }

    [Fact]
    public async Task A_negative_price_is_refused()
    {
        var t = Template(_designerId);
        Existing(t);

        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Sut().SetPricingAsync(t.Id, new SetTemplatePricingRequest(UsagePrice: -1m, CommissionPrice: null)));
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
    /// A dedicated template that was issued to a customer is promised to them — their "it's ready"
    /// link resolves through this row even though no campaign exists yet.
    /// </summary>
    [Fact]
    public async Task A_template_issued_to_a_customer_is_unlisted_rather_than_deleted()
    {
        AsAdmin();
        var t = Template();
        Existing(t);
        _inquiries.Query(Arg.Any<bool>()).Returns(new[]
        {
            new Inquiry
            {
                Id = Guid.NewGuid(), Name = "Aisha", Email = "a@test.com", Occasion = "Wedding",
                Message = "", IssuedTemplateId = t.Id, TemplateIssued = true,
            },
        }.AsAsyncQueryable());

        var result = await Sut().DeleteAsync(t.Id);

        Assert.False(result.Deleted);
        Assert.True(result.Unlisted);
        _templates.DidNotReceive().Remove(Arg.Any<Template>());
    }

    /// <summary>
    /// Nothing enforces these ids at the database level, so a delete has to cut the link itself —
    /// otherwise the designer's dashboard offers to revise a template that is gone.
    /// </summary>
    [Fact]
    public async Task Deleting_a_template_clears_the_submission_that_published_it()
    {
        var t = Template(_designerId);
        Existing(t);
        var submission = new CustomTemplate
        {
            Id = Guid.NewGuid(), DesignerUserId = _designerId, Name = "Aurora Vows",
            Slug = "aurora-vows", Category = "Wedding", PublishedTemplateId = t.Id,
        };
        _submissions.Query(Arg.Any<bool>()).Returns(new[] { submission }.AsAsyncQueryable());

        var result = await Sut().DeleteAsync(t.Id);

        Assert.True(result.Deleted);
        Assert.Null(submission.PublishedTemplateId);
        _templates.Received(1).Remove(t);
    }
}
