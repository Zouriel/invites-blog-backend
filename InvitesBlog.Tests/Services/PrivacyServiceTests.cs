using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions.Privacy;
using InvitesBlog.Application.Security;
using InvitesBlog.Application.Services.Privacy;
using InvitesBlog.Domain.Entities;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class PrivacyServiceTests
{
    private readonly IInviteRepository _invites = Substitute.For<IInviteRepository>();
    private readonly IGuestRepository _guests = Substitute.For<IGuestRepository>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly ISuppressionRepository _suppression = Substitute.For<ISuppressionRepository>();
    private readonly IRepository<AuditLog> _auditLogs = Substitute.For<IRepository<AuditLog>>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    private PrivacyService Sut() => new(_invites, _guests, _campaigns, _suppression, _auditLogs, _uow);

    private const string Token = "removal-token";
    private static string Hash => TokenService.Hash(Token);

    [Fact]
    public async Task GetRemovalInfo_empty_token_throws()
    {
        await Assert.ThrowsAsync<PrivacyInviteNotFoundException>(() => Sut().GetRemovalInfoAsync(""));
    }

    [Fact]
    public async Task GetRemovalInfo_unknown_token_throws()
    {
        _invites.GetByTokenHashAsync(Hash, Arg.Any<CancellationToken>()).Returns((Invite?)null);
        await Assert.ThrowsAsync<PrivacyInviteNotFoundException>(() => Sut().GetRemovalInfoAsync(Token));
    }

    [Fact]
    public async Task GetRemovalInfo_success_returns_dto()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id);
        var invite = TestData.Invite(campaign.Id, guest.Id, tokenHash: Hash);
        _invites.GetByTokenHashAsync(Hash, Arg.Any<CancellationToken>()).Returns(invite);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);

        var dto = await Sut().GetRemovalInfoAsync(Token);

        Assert.Equal(guest.Name, dto.GuestName);
        Assert.Equal(campaign.Title, dto.EventTitle);
        Assert.True(dto.HasEmail);
        Assert.True(dto.HasPhone);
        Assert.False(dto.AlreadyRemoved);
    }

    [Fact]
    public async Task Remove_already_opted_out_is_idempotent_and_adds_no_suppression()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id);
        guest.OptedOut = true;
        var invite = TestData.Invite(campaign.Id, guest.Id, tokenHash: Hash);
        _invites.GetByTokenHashAsync(Hash, Arg.Any<CancellationToken>()).Returns(invite);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);

        var res = await Sut().RemoveAsync(Token);

        Assert.True(res.Removed);
        await _suppression.DidNotReceive().AddAsync(Arg.Any<SuppressionEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Remove_success_suppresses_contacts_anonymizes_and_audits()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id, email: "leaver@test.com", phone: "+9601234567");
        var invite = TestData.Invite(campaign.Id, guest.Id, tokenHash: Hash);
        _invites.GetByTokenHashAsync(Hash, Arg.Any<CancellationToken>()).Returns(invite);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        _suppression.ExistsByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        var res = await Sut().RemoveAsync(Token);

        Assert.True(res.Removed);
        // One suppression entry each for email + phone.
        await _suppression.Received(2).AddAsync(Arg.Any<SuppressionEntry>(), Arg.Any<CancellationToken>());
        await _auditLogs.Received(1).AddAsync(Arg.Any<AuditLog>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        Assert.True(guest.OptedOut);
        Assert.Null(guest.Email);
        Assert.Null(guest.PhoneE164);
        Assert.Equal("Removed guest", guest.Name);
    }
}
