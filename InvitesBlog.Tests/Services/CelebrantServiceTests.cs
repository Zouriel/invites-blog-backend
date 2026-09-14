using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.Celebrants;
using InvitesBlog.Domain.Entities;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>The people an event is for: who may add them, and what adding one does.</summary>
public class CelebrantServiceTests
{
    private readonly ICampaignOwnershipService _ownership = Substitute.For<ICampaignOwnershipService>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly IRepository<CampaignCelebrant> _celebrants = TestData.NoCelebrants();
    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly Campaign _campaign = TestData.Campaign();
    private readonly AppUser _me = new() { Id = Guid.NewGuid(), Email = "host@test.com", DisplayName = "Host" };

    public CelebrantServiceTests()
    {
        _currentUser.UserId.Returns(_me.Id);
        _users.GetByIdAsync(_me.Id, Arg.Any<CancellationToken>()).Returns(_me);
        _campaigns.GetByIdAsync(_campaign.Id, Arg.Any<CancellationToken>()).Returns(_campaign);
        _ownership.AccessAsync(_campaign.Id, Arg.Any<CancellationToken>()).Returns(CampaignAccess.Organiser);
    }

    private CelebrantService Sut() => new(
        _ownership, _campaigns, _celebrants, _users, Substitute.For<IInviterRepository>(), _currentUser, _email, new PhoneNormalizer(), _uow,
        Substitute.For<IConfiguration>());

    [Fact]
    public async Task Adding_someone_stores_them_without_emailing_by_default()
    {
        await Sut().AddAsync(_campaign.Id, new AddCelebrantRequest("Amira", "Amira@Test.com", null));

        await _celebrants.Received(1).AddAsync(
            Arg.Is<CampaignCelebrant>(c => c.Email == "amira@test.com" && !c.CanManage), Arg.Any<CancellationToken>());
        await _email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Ticking_let_them_know_sends_the_heads_up()
    {
        await Sut().AddAsync(_campaign.Id, new AddCelebrantRequest("Amira", "amira@test.com", null, Notify: true));

        await _email.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.To == "amira@test.com"), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Adding_yourself_changes_nothing()
    {
        await Sut().AddAsync(_campaign.Id, new AddCelebrantRequest("Me", "HOST@test.com", null));

        await _celebrants.DidNotReceive().AddAsync(Arg.Any<CampaignCelebrant>(), Arg.Any<CancellationToken>());
    }

    /// <summary>The builder calls with the campaign's possession token, so there is no signed-in account to compare.</summary>
    [Fact]
    public async Task Adding_the_organiser_is_ignored_even_without_an_account_on_the_call()
    {
        _currentUser.UserId.Returns((Guid?)null);
        _campaign.CreatedByUserId = _me.Id;

        await Sut().AddAsync(_campaign.Id, new AddCelebrantRequest("Me", "host@test.com", null));

        await _celebrants.DidNotReceive().AddAsync(Arg.Any<CampaignCelebrant>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_celebrant_without_email_or_phone_is_refused()
    {
        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().AddAsync(_campaign.Id, new AddCelebrantRequest("Amira", " ", null)));
    }

    [Fact]
    public async Task A_read_only_celebrant_cannot_add_others()
    {
        _ownership.AccessAsync(_campaign.Id, Arg.Any<CancellationToken>()).Returns(CampaignAccess.Celebrant);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Sut().AddAsync(_campaign.Id, new AddCelebrantRequest("Amira", "amira@test.com", null)));
    }

    /// <summary>Full access is the organiser's to give. A celebrant with it can't hand it on.</summary>
    [Fact]
    public async Task Only_the_organiser_grants_full_access()
    {
        _ownership.AccessAsync(_campaign.Id, Arg.Any<CancellationToken>()).Returns(CampaignAccess.Manager);

        await Assert.ThrowsAsync<ForbiddenException>(
            () => Sut().SetAccessAsync(_campaign.Id, Guid.NewGuid(), new SetCelebrantAccessRequest(true)));
    }
}
