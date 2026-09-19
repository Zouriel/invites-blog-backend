using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Services.Studio;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>A Studio account giving the passes it holds to its clients' events.</summary>
public class StudioServiceTests
{
    private readonly Guid _me = Guid.NewGuid();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IPlanService _plans = Substitute.For<IPlanService>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly IInviterRepository _inviters = Substitute.For<IInviterRepository>();
    private readonly List<PassCredit> _credits = [];
    private readonly IRepository<PassCredit> _creditRepo = Substitute.For<IRepository<PassCredit>>();

    public StudioServiceTests()
    {
        _currentUser.UserId.Returns(_me);
        _plans.IsStudioAsync(_me, Arg.Any<CancellationToken>()).Returns(true);
        _templates.Query(Arg.Any<bool>()).Returns(Array.Empty<Template>().AsAsyncQueryable());
        _inviters.Query(Arg.Any<bool>()).Returns(Array.Empty<Inviter>().AsAsyncQueryable());
        _creditRepo.Query(Arg.Any<bool>()).Returns(_ => _credits.AsAsyncQueryable());
    }

    private StudioService Sut() => new(
        _currentUser, _plans, _campaigns, _templates, _inviters,
        TestData.Empty<Guest>(), TestData.Empty<Invite>(), _creditRepo, TestData.Empty<AuditLog>(),
        Substitute.For<IUnitOfWork>(), Substitute.For<InvitesBlog.Application.Services.MediaBuckets.IMediaBucketService>());

    private Campaign Mine()
    {
        var c = TestData.Campaign();
        c.CreatedByUserId = _me;
        _campaigns.Query(Arg.Any<bool>()).Returns(new[] { c }.AsAsyncQueryable());
        return c;
    }

    [Fact]
    public async Task Giving_a_pass_puts_it_on_the_event_and_uses_one_credit()
    {
        var campaign = Mine();
        _credits.Add(new PassCredit { Id = Guid.NewGuid(), OwnerUserId = _me, Kind = EventPassKind.Wedding, CreatedAt = DateTimeOffset.UtcNow });

        var result = await Sut().GivePassAsync(campaign.Id, new GivePassRequest("Wedding"));

        Assert.Equal(EventPassKind.Wedding, campaign.EventPass);
        Assert.Equal("Wedding", result.Pass);
        Assert.Equal(campaign.Id, _credits[0].UsedOnCampaignId);
    }

    [Fact]
    public async Task Without_a_credit_of_that_kind_nothing_is_given()
    {
        var campaign = Mine();
        _credits.Add(new PassCredit { Id = Guid.NewGuid(), OwnerUserId = _me, Kind = EventPassKind.Party, CreatedAt = DateTimeOffset.UtcNow });

        var e = await Assert.ThrowsAsync<BusinessRuleException>(() => Sut().GivePassAsync(campaign.Id, new GivePassRequest("Wedding")));
        Assert.Equal("no_pass_credits", e.ErrorCode);
        Assert.Equal(EventPassKind.None, campaign.EventPass);
    }

    [Fact]
    public async Task Only_a_studio_account_has_a_studio()
    {
        _plans.IsStudioAsync(_me, Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().OverviewAsync());
    }
}
