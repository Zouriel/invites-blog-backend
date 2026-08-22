using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// A campaign answers to two keys — the emailed possession token and the account that booked it.
/// Only the first existed at one point, so opening your own campaign from Sent while signed in was
/// refused outright; these pin both doors open and everything else shut.
/// </summary>
public class CampaignOwnershipServiceTests
{
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly IInviterRepository _inviters = Substitute.For<IInviterRepository>();

    private CampaignOwnershipService Sut() => new(_currentUser, _users, _campaigns, _inviters);

    private static AppUser Account(string? email = "host@test.com", string? phone = null) => new()
    {
        Id = Guid.NewGuid(), Email = email, PhoneE164 = phone,
        DisplayName = "Host", IsActive = true, CreatedAt = DateTimeOffset.UtcNow
    };

    private Campaign BookedBy(string? email, string? phone = null)
    {
        var inviter = new Inviter
        {
            Id = Guid.NewGuid(), Name = "Host", Email = email, PhoneE164 = phone,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var campaign = TestData.Campaign();
        campaign.InviterId = inviter.Id;

        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        _inviters.GetByIdAsync(inviter.Id, Arg.Any<CancellationToken>()).Returns(inviter);
        return campaign;
    }

    private void SignedInAs(AppUser account)
    {
        _currentUser.UserId.Returns(account.Id);
        _users.GetByIdAsync(account.Id, Arg.Any<CancellationToken>()).Returns(account);
    }

    [Fact]
    public async Task A_possession_token_for_this_campaign_still_grants_access_with_no_account()
    {
        var campaign = TestData.Campaign();
        _currentUser.CampaignId.Returns(campaign.Id);
        _currentUser.UserId.Returns((Guid?)null);

        Assert.True(await Sut().OwnsAsync(campaign.Id));
    }

    [Fact]
    public async Task A_possession_token_for_a_DIFFERENT_campaign_grants_nothing()
    {
        _currentUser.CampaignId.Returns(Guid.NewGuid());

        Assert.False(await Sut().OwnsAsync(BookedBy("host@test.com").Id));
    }

    [Fact]
    public async Task The_account_that_booked_it_owns_it_without_any_token()
    {
        SignedInAs(Account(email: "host@test.com"));

        Assert.True(await Sut().OwnsAsync(BookedBy("host@test.com").Id));
    }

    [Fact]
    public async Task A_campaign_booked_with_the_account_s_phone_is_owned_too()
    {
        SignedInAs(Account(email: "host@test.com", phone: "+9607777777"));

        Assert.True(await Sut().OwnsAsync(BookedBy(email: null, phone: "+9607777777").Id));
    }

    [Fact]
    public async Task Email_case_does_not_split_ownership()
    {
        SignedInAs(Account(email: "host@test.com"));

        Assert.True(await Sut().OwnsAsync(BookedBy("HOST@Test.com").Id));
    }

    [Fact]
    public async Task Someone_else_s_campaign_is_not_owned()
    {
        SignedInAs(Account(email: "host@test.com"));

        Assert.False(await Sut().OwnsAsync(BookedBy("someone.else@test.com").Id));
    }

    [Fact]
    public async Task An_account_with_no_identifiers_owns_nothing()
    {
        // Guards the empty-matches-empty trap: a campaign booked with no email must not fall to
        // whoever happens to have no email either.
        SignedInAs(Account(email: null, phone: null));

        Assert.False(await Sut().OwnsAsync(BookedBy(email: null, phone: null).Id));
    }

    [Fact]
    public async Task A_campaign_with_no_inviter_is_owned_by_nobody()
    {
        SignedInAs(Account(email: "host@test.com"));
        var orphan = TestData.Campaign();
        _campaigns.GetByIdAsync(orphan.Id, Arg.Any<CancellationToken>()).Returns(orphan);

        Assert.False(await Sut().OwnsAsync(orphan.Id));
    }

    [Fact]
    public async Task An_anonymous_caller_owns_nothing()
    {
        _currentUser.UserId.Returns((Guid?)null);

        Assert.False(await Sut().OwnsAsync(BookedBy("host@test.com").Id));
    }
}
