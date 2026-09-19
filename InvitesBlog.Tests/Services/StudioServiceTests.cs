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

/// <summary>
/// A Studio account's clients, and the discount they get: automatic, on a design the Studio made for
/// them. There is no stock of passes to give any more.
/// </summary>
public class StudioServiceTests
{
    private readonly Guid _me = Guid.NewGuid();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IPlanService _plans = Substitute.For<IPlanService>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly IInviterRepository _inviters = Substitute.For<IInviterRepository>();
    private readonly List<Template> _templateRows = [];

    public StudioServiceTests()
    {
        _currentUser.UserId.Returns(_me);
        _plans.IsStudioAsync(_me, Arg.Any<CancellationToken>()).Returns(true);
        _templates.Query(Arg.Any<bool>()).Returns(_ => _templateRows.AsAsyncQueryable());
        _inviters.Query(Arg.Any<bool>()).Returns(Array.Empty<Inviter>().AsAsyncQueryable());
        _campaigns.Query(Arg.Any<bool>()).Returns(Array.Empty<Campaign>().AsAsyncQueryable());
    }

    private StudioService Sut() => new(
        _currentUser, _plans, _campaigns, _templates, _inviters,
        TestData.Empty<Guest>(), TestData.Empty<Invite>(), TestData.PriceBook(), _offers);

    private readonly IPassOfferService _offers = Substitute.For<IPassOfferService>();

    private static PassOfferDto Offer(int percent) =>
        new(139m, 489m, 199m, 699m, percent, percent > 0 ? "Me" : null, 99m, 349m);

    [Fact]
    public async Task Clients_on_a_design_made_for_them_are_marked_discounted()
    {
        var forClient = new Template { Id = Guid.NewGuid(), Name = "For Aisha", DesignerUserId = _me, AssignedEmail = "aisha@x.mv", Version = "1", Visibility = TemplateVisibility.Dedicated };
        _templateRows.Add(forClient);
        var theirs = TestData.Campaign();
        theirs.TemplateId = forClient.Id;
        var mine = TestData.Campaign();
        mine.CreatedByUserId = _me;
        _campaigns.Query(Arg.Any<bool>()).Returns(new[] { theirs, mine }.AsAsyncQueryable());
        _offers.ForCampaignAsync(theirs.Id, Arg.Any<CancellationToken>()).Returns(Offer(30));
        _offers.ForCampaignAsync(mine.Id, Arg.Any<CancellationToken>()).Returns(Offer(0));

        var o = await Sut().OverviewAsync();

        Assert.Equal(30, o.DiscountPercent);
        Assert.Equal(139m, o.PartyPassPrice);
        Assert.Equal(489m, o.WeddingPassPrice);
        Assert.True(o.Clients.Single(c => c.CampaignId == theirs.Id).Discounted);
        Assert.False(o.Clients.Single(c => c.CampaignId == mine.Id).Discounted);
    }

    [Fact]
    public async Task Only_a_studio_account_has_a_studio()
    {
        _plans.IsStudioAsync(_me, Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<ForbiddenException>(() => Sut().OverviewAsync());
    }
}
