using System.Text.Json.Nodes;
using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Invites;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Invites;
using InvitesBlog.Application.Security;
using InvitesBlog.Application.Services.Invites;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class InviteServiceTests
{
    private readonly IInviteRepository _invites = Substitute.For<IInviteRepository>();
    private readonly IGuestRepository _guests = Substitute.For<IGuestRepository>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly IInviterRepository _inviters = Substitute.For<IInviterRepository>();
    private readonly IRepository<RsvpResponse> _rsvp = Substitute.For<IRepository<RsvpResponse>>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private IValidator<RsvpRequest> _rsvpValidator = TestData.PassingValidator<RsvpRequest>();

    private InviteService Sut() => new(
        _invites, _guests, _campaigns, _templates, _inviters, _rsvp, _uow, _currentUser, _config, _rsvpValidator);

    private static readonly InviteRenderer Renderer = (c, t, g, i, link, n, p, e) =>
        new InviteRenderData(t.PackageUrl, new JsonObject { ["guest"] = g.Name }, false, c.Status.ToString());

    // ----- GetByToken -----

    [Fact]
    public async Task GetByToken_unknown_throws_NotFound()
    {
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Invite?)null);
        await Assert.ThrowsAsync<InviteNotFoundException>(() => Sut().GetByTokenAsync("tok", Renderer));
    }

    [Fact]
    public async Task GetByToken_cancelled_campaign_returns_cancelled_response()
    {
        var campaign = TestData.Campaign(status: CampaignStatus.Cancelled);
        var invite = TestData.Invite(campaign.Id, Guid.NewGuid());
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);

        var res = await Sut().GetByTokenAsync("tok", Renderer);

        Assert.IsType<InviteCancelledResponse>(res);
    }

    [Fact]
    public async Task GetByToken_requires_otp_returns_requiresOtp_response()
    {
        var campaign = TestData.Campaign();
        var invite = TestData.Invite(campaign.Id, Guid.NewGuid(), requiresOtp: true);
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);

        var res = await Sut().GetByTokenAsync("tok", Renderer);

        Assert.IsType<InviteRequiresOtpResponse>(res);
    }

    // ----- GetMyInvite (shared /e/{id} link, guest-list-only) -----

    [Fact]
    public async Task GetMyInvite_matched_email_renders_personalized_invite()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id, email: "guest@test.com");
        var template = TestData.Template();
        _currentUser.Contact.Returns("guest@test.com");
        _currentUser.ContactType.Returns("email");
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        _guests.ListByCampaignAsync(campaign.Id, false, Arg.Any<CancellationToken>()).Returns(new[] { guest });
        _invites.GetByGuestIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns((Invite?)null); // created lazily
        _templates.GetByIdAsync(campaign.TemplateId, Arg.Any<CancellationToken>()).Returns(template);

        var res = await Sut().GetMyInviteAsync(campaign.Id, Renderer);

        var view = Assert.IsType<MyInviteResponse>(res);
        Assert.Equal(template.PackageUrl, view.PackageUrl);
        await _invites.Received(1).AddAsync(Arg.Any<Invite>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetMyInvite_email_not_on_guest_list_is_refused()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id, email: "someone@test.com");
        _currentUser.Contact.Returns("notlisted@test.com");
        _currentUser.ContactType.Returns("email");
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        _guests.ListByCampaignAsync(campaign.Id, false, Arg.Any<CancellationToken>()).Returns(new[] { guest });

        await Assert.ThrowsAsync<InviteNotFoundException>(() => Sut().GetMyInviteAsync(campaign.Id, Renderer));
    }

    [Fact]
    public async Task GetByToken_success_marks_viewed_and_returns_view()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id);
        var template = TestData.Template();
        var invite = TestData.Invite(campaign.Id, guest.Id, status: InviteStatus.Sent);
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);
        _templates.GetByIdAsync(campaign.TemplateId, Arg.Any<CancellationToken>()).Returns(template);

        var res = await Sut().GetByTokenAsync("tok", Renderer);

        var view = Assert.IsType<InviteViewResponse>(res);
        Assert.Equal(template.PackageUrl, view.PackageUrl);
        Assert.NotNull(invite.ViewedAt);
        Assert.Equal(InviteStatus.Viewed, invite.Status);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ----- Rsvp -----

    [Fact]
    public async Task Rsvp_validation_failure_throws()
    {
        _rsvpValidator = TestData.FailingValidator<RsvpRequest>();
        await Assert.ThrowsAsync<ValidationException>(
            () => Sut().RsvpAsync("tok", new RsvpRequest("Going", null, null, null, null, null)));
    }

    [Fact]
    public async Task Rsvp_unknown_token_throws()
    {
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Invite?)null);
        await Assert.ThrowsAsync<InviteNotFoundException>(
            () => Sut().RsvpAsync("tok", new RsvpRequest("Going", null, null, null, null, null)));
    }

    [Fact]
    public async Task Rsvp_invalid_status_throws()
    {
        var invite = TestData.Invite(Guid.NewGuid(), Guid.NewGuid());
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        await Assert.ThrowsAsync<InvalidRsvpStatusException>(
            () => Sut().RsvpAsync("tok", new RsvpRequest("Teleporting", null, null, null, null, null)));
    }

    [Fact]
    public async Task Rsvp_success_records_response()
    {
        var invite = TestData.Invite(Guid.NewGuid(), Guid.NewGuid());
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);

        var res = await Sut().RsvpAsync("tok", new RsvpRequest("Going", 2, null, null, null, null));

        Assert.Equal("Going", res.Rsvp);
        Assert.Equal(RsvpStatus.Going, invite.RsvpStatus);
        await _rsvp.Received(1).AddAsync(Arg.Any<RsvpResponse>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ----- Inbox / Claim -----

    [Fact]
    public async Task Inbox_no_contact_throws_Unauthorized()
    {
        _currentUser.Contact.Returns((string?)null);
        await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().GetInboxAsync());
    }

    [Fact]
    public async Task Inbox_returns_cards_for_verified_contact()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id, email: "me@test.com", phone: null);
        var invite = TestData.Invite(campaign.Id, guest.Id, rsvp: RsvpStatus.Going);
        _currentUser.Contact.Returns("me@test.com");
        _currentUser.ContactType.Returns("email");
        _guests.Query().Returns(new[] { guest }.AsAsyncQueryable());
        _invites.Query().Returns(new[] { invite }.AsAsyncQueryable());
        _campaigns.Query().Returns(new[] { campaign }.AsAsyncQueryable());

        var cards = await Sut().GetInboxAsync();

        var card = Assert.Single(cards);
        Assert.Equal(campaign.Title, card.EventTitle);
        Assert.Equal("Going", card.RsvpStatus);
    }

    [Fact]
    public async Task Claim_no_contact_throws_Unauthorized()
    {
        _currentUser.Contact.Returns((string?)null);
        await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().ClaimAsync("tok"));
    }

    [Fact]
    public async Task Claim_unknown_invite_throws()
    {
        _currentUser.Contact.Returns("me@test.com");
        _currentUser.ContactType.Returns("email");
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Invite?)null);
        await Assert.ThrowsAsync<InviteNotFoundException>(() => Sut().ClaimAsync("tok"));
    }

    [Fact]
    public async Task Claim_success_links_contact_to_guest()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id, email: null, phone: null);
        var invite = TestData.Invite(campaign.Id, guest.Id);
        _currentUser.Contact.Returns("me@test.com");
        _currentUser.ContactType.Returns("email");
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);

        var res = await Sut().ClaimAsync("raw-token");

        Assert.True(res.Claimed);
        Assert.Equal("me@test.com", guest.Email);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RsvpByInviteId_rejects_non_owner()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id, email: "owner@test.com", phone: null);
        var invite = TestData.Invite(campaign.Id, guest.Id);
        _currentUser.Contact.Returns("someone-else@test.com");
        _currentUser.ContactType.Returns("email");
        _invites.GetByIdAsync(invite.Id, Arg.Any<CancellationToken>()).Returns(invite);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);

        await Assert.ThrowsAsync<InviteNotFoundException>(
            () => Sut().RsvpByInviteIdAsync(invite.Id, new RsvpRequest("Going", 1, null, null, null, null)));
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RsvpByInviteId_owner_records_rsvp()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id, email: "owner@test.com", phone: null);
        var invite = TestData.Invite(campaign.Id, guest.Id);
        _currentUser.Contact.Returns("owner@test.com");
        _currentUser.ContactType.Returns("email");
        _invites.GetByIdAsync(invite.Id, Arg.Any<CancellationToken>()).Returns(invite);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);

        var res = await Sut().RsvpByInviteIdAsync(invite.Id, new RsvpRequest("Going", 1, null, null, null, null));

        Assert.Equal("Going", res.Rsvp);
        Assert.Equal(RsvpStatus.Going, invite.RsvpStatus);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
