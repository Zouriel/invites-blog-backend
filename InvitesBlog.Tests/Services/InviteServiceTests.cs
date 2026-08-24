using System.Text.Json.Nodes;
using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Invites;
using InvitesBlog.Application.Dtos.Otp;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Invites;
using InvitesBlog.Application.Security;
using InvitesBlog.Application.Services.Invites;
using InvitesBlog.Application.Services.Otp;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class InviteServiceTests
{
    private const string Ip1 = "203.0.113.10";
    private const string Ip2 = "203.0.113.20";
    private const string Ip3 = "203.0.113.30";
    private const string Ip4 = "203.0.113.40";

    private readonly IInviteRepository _invites = Substitute.For<IInviteRepository>();
    private readonly IGuestRepository _guests = Substitute.For<IGuestRepository>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly IInviterRepository _inviters = Substitute.For<IInviterRepository>();
    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly IRepository<RsvpResponse> _rsvp = Substitute.For<IRepository<RsvpResponse>>();
    private readonly IRepository<VerifiedContactLink> _contactLinks = Substitute.For<IRepository<VerifiedContactLink>>();
    private readonly IRepository<InviteTrustedIp> _trustedIps = Substitute.For<IRepository<InviteTrustedIp>>();
    private readonly IOtpService _otp = Substitute.For<IOtpService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private IValidator<RsvpRequest> _rsvpValidator = TestData.PassingValidator<RsvpRequest>();

    /// <summary>Backs _trustedIps with an in-memory list so ListAsync/AddAsync/Remove actually behave
    /// like a real repository across a test — the eviction/bump logic depends on that, not just on
    /// individual calls being received.</summary>
    private readonly List<InviteTrustedIp> _trustedIpRows = [];

    public InviteServiceTests()
    {
        _trustedIps.ListAsync(Arg.Any<System.Linq.Expressions.Expression<Func<InviteTrustedIp, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var predicate = ci.Arg<System.Linq.Expressions.Expression<Func<InviteTrustedIp, bool>>>().Compile();
                return (IReadOnlyList<InviteTrustedIp>)_trustedIpRows.Where(predicate).ToList();
            });
        _trustedIps.AddAsync(Arg.Any<InviteTrustedIp>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _trustedIpRows.Add(ci.Arg<InviteTrustedIp>()); return Task.CompletedTask; });
        _trustedIps.When(x => x.Remove(Arg.Any<InviteTrustedIp>()))
            .Do(ci => _trustedIpRows.Remove(ci.Arg<InviteTrustedIp>()));
    }

    private InviteService Sut() => new(
        _invites, _guests, _campaigns, _templates, _inviters, _users, _rsvp, _contactLinks, _trustedIps, _otp, _uow,
        _currentUser, _config, _rsvpValidator);

    private static readonly InviteRenderer Renderer = (c, t, g, i, link, n, p, e) =>
        new InviteRenderData(t.PackageUrl, new JsonObject { ["guest"] = g.Name }, false, c.Status.ToString());

    // ----- GetByToken -----

    [Fact]
    public async Task GetByToken_unknown_throws_NotFound()
    {
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Invite?)null);
        await Assert.ThrowsAsync<InviteNotFoundException>(() => Sut().GetByTokenAsync("tok", Ip1, Renderer));
    }

    [Fact]
    public async Task GetByToken_cancelled_campaign_returns_cancelled_response()
    {
        var campaign = TestData.Campaign(status: CampaignStatus.Cancelled);
        var invite = TestData.Invite(campaign.Id, Guid.NewGuid());
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);

        var res = await Sut().GetByTokenAsync("tok", Ip1, Renderer);

        Assert.IsType<InviteCancelledResponse>(res);
    }

    // ----- Personal-link IP binding -----

    [Fact]
    public async Task GetByToken_no_ip_visible_returns_requiresOtp_response()
    {
        // Forwarded-headers misconfigured / not behind the expected proxy — never silently trust.
        var campaign = TestData.Campaign();
        var invite = TestData.Invite(campaign.Id, Guid.NewGuid());
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);

        var res = await Sut().GetByTokenAsync("tok", null, Renderer);

        Assert.IsType<InviteRequiresOtpResponse>(res);
    }

    [Fact]
    public async Task GetByToken_first_ever_open_auto_trusts_and_renders()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id);
        var template = TestData.Template();
        var invite = TestData.Invite(campaign.Id, guest.Id);
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);
        _templates.GetByIdAsync(campaign.TemplateId, Arg.Any<CancellationToken>()).Returns(template);

        var res = await Sut().GetByTokenAsync("tok", Ip1, Renderer);

        Assert.IsType<InviteViewResponse>(res);
        var saved = Assert.Single(_trustedIpRows);
        Assert.Equal(Ip1, saved.IpAddress);
        Assert.Equal(invite.Id, saved.InviteId);
    }

    [Fact]
    public async Task GetByToken_from_already_trusted_ip_renders_without_otp()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id);
        var template = TestData.Template();
        var invite = TestData.Invite(campaign.Id, guest.Id);
        _trustedIpRows.Add(new InviteTrustedIp
        {
            Id = Guid.NewGuid(), InviteId = invite.Id, IpAddress = Ip1,
            FirstSeenAt = DateTimeOffset.UtcNow.AddDays(-1), LastSeenAt = DateTimeOffset.UtcNow.AddDays(-1),
        });
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);
        _templates.GetByIdAsync(campaign.TemplateId, Arg.Any<CancellationToken>()).Returns(template);

        var res = await Sut().GetByTokenAsync("tok", Ip1, Renderer);

        Assert.IsType<InviteViewResponse>(res);
        var saved = Assert.Single(_trustedIpRows); // still just the one row — bumped, not duplicated
        Assert.True(saved.LastSeenAt > saved.FirstSeenAt);
    }

    [Fact]
    public async Task GetByToken_from_unrecognized_ip_after_first_trust_requires_otp()
    {
        var campaign = TestData.Campaign();
        var invite = TestData.Invite(campaign.Id, Guid.NewGuid());
        _trustedIpRows.Add(new InviteTrustedIp
        {
            Id = Guid.NewGuid(), InviteId = invite.Id, IpAddress = Ip1,
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        });
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);

        var res = await Sut().GetByTokenAsync("tok", Ip2, Renderer);

        Assert.IsType<InviteRequiresOtpResponse>(res);
        Assert.Single(_trustedIpRows); // the new IP was NOT trusted — only reauth can add it
    }

    [Fact]
    public async Task RequestReauth_sends_to_guests_own_email_not_a_caller_supplied_one()
    {
        var guest = TestData.Guest(Guid.NewGuid(), email: "guest@test.com", phone: "+9607771234");
        var invite = TestData.Invite(guest.CampaignId, guest.Id);
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);
        _otp.RequestAsync(Arg.Any<SendOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OtpChallengeResponse(Guid.NewGuid(), 300));

        await Sut().RequestReauthAsync("tok");

        await _otp.Received(1).RequestAsync(
            Arg.Is<SendOtpRequest>(r => r.Channel == "email" && r.Email == "guest@test.com"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestReauth_falls_back_to_phone_when_no_email_on_file()
    {
        var guest = TestData.Guest(Guid.NewGuid(), email: null, phone: "+9607771234");
        var invite = TestData.Invite(guest.CampaignId, guest.Id);
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);
        _otp.RequestAsync(Arg.Any<SendOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new OtpChallengeResponse(Guid.NewGuid(), 300));

        await Sut().RequestReauthAsync("tok");

        await _otp.Received(1).RequestAsync(
            Arg.Is<SendOtpRequest>(r => r.Channel == "sms" && r.Phone == "+9607771234"),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyReauth_trusts_the_ip_and_does_not_mint_an_account_jwt()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id);
        var template = TestData.Template();
        var invite = TestData.Invite(campaign.Id, guest.Id);
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);
        _templates.GetByIdAsync(campaign.TemplateId, Arg.Any<CancellationToken>()).Returns(template);
        _otp.VerifyContactAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifiedContact("email", guest.Email!));

        var res = await Sut().VerifyReauthAsync("tok", Ip2, new VerifyOtpRequest(Guid.NewGuid(), "123456"), Renderer);

        Assert.IsType<InviteViewResponse>(res);
        var saved = Assert.Single(_trustedIpRows);
        Assert.Equal(Ip2, saved.IpAddress);
        // The whole point (per IInviteService.VerifyReauthAsync docs): scoped to this invite only —
        // never routes through OtpService.VerifyAsync, which is the one that mints a 30-day JWT.
        await _otp.DidNotReceive().VerifyAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task VerifyReauth_evicts_the_least_recently_seen_ip_when_already_at_three()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id);
        var template = TestData.Template();
        var invite = TestData.Invite(campaign.Id, guest.Id);
        var now = DateTimeOffset.UtcNow;
        _trustedIpRows.AddRange(
        [
            new InviteTrustedIp { Id = Guid.NewGuid(), InviteId = invite.Id, IpAddress = Ip1, FirstSeenAt = now.AddDays(-3), LastSeenAt = now.AddDays(-3) },
            new InviteTrustedIp { Id = Guid.NewGuid(), InviteId = invite.Id, IpAddress = Ip2, FirstSeenAt = now.AddDays(-2), LastSeenAt = now.AddDays(-1) },
            new InviteTrustedIp { Id = Guid.NewGuid(), InviteId = invite.Id, IpAddress = Ip3, FirstSeenAt = now.AddDays(-1), LastSeenAt = now },
        ]);
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);
        _templates.GetByIdAsync(campaign.TemplateId, Arg.Any<CancellationToken>()).Returns(template);
        _otp.VerifyContactAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifiedContact("email", guest.Email!));

        await Sut().VerifyReauthAsync("tok", Ip4, new VerifyOtpRequest(Guid.NewGuid(), "123456"), Renderer);

        Assert.Equal(3, _trustedIpRows.Count);
        Assert.DoesNotContain(_trustedIpRows, r => r.IpAddress == Ip1); // oldest LastSeenAt — evicted
        Assert.Contains(_trustedIpRows, r => r.IpAddress == Ip2);
        Assert.Contains(_trustedIpRows, r => r.IpAddress == Ip3);
        Assert.Contains(_trustedIpRows, r => r.IpAddress == Ip4);
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

        var res = await Sut().GetByTokenAsync("tok", Ip1, Renderer);

        var view = Assert.IsType<InviteViewResponse>(res);
        Assert.Equal(template.PackageUrl, view.PackageUrl);
        Assert.NotNull(invite.ViewedAt);
        Assert.Equal(InviteStatus.Viewed, invite.Status);
        // Two separate persists: trusting the first-ever IP, then marking the invite viewed.
        await _uow.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ----- Rsvp -----

    [Fact]
    public async Task Rsvp_validation_failure_throws()
    {
        _rsvpValidator = TestData.FailingValidator<RsvpRequest>();
        await Assert.ThrowsAsync<ValidationException>(
            () => Sut().RsvpAsync("tok", Ip1, new RsvpRequest("Going", null, null, null, null, null)));
    }

    [Fact]
    public async Task Rsvp_unknown_token_throws()
    {
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((Invite?)null);
        await Assert.ThrowsAsync<InviteNotFoundException>(
            () => Sut().RsvpAsync("tok", Ip1, new RsvpRequest("Going", null, null, null, null, null)));
    }

    [Fact]
    public async Task Rsvp_from_untrusted_ip_throws_Unauthorized()
    {
        var invite = TestData.Invite(Guid.NewGuid(), Guid.NewGuid());
        _trustedIpRows.Add(new InviteTrustedIp
        {
            Id = Guid.NewGuid(), InviteId = invite.Id, IpAddress = Ip1,
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        });
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);

        // Same invite already has a trusted IP (Ip1) — RSVPing from a DIFFERENT, untrusted one (Ip2)
        // must be refused exactly like viewing would be, or the reauth challenge is pointless.
        await Assert.ThrowsAsync<UnauthorizedException>(
            () => Sut().RsvpAsync("tok", Ip2, new RsvpRequest("Going", null, null, null, null, null)));
    }

    [Fact]
    public async Task Rsvp_invalid_status_throws()
    {
        var invite = TestData.Invite(Guid.NewGuid(), Guid.NewGuid());
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);
        await Assert.ThrowsAsync<InvalidRsvpStatusException>(
            () => Sut().RsvpAsync("tok", Ip1, new RsvpRequest("Teleporting", null, null, null, null, null)));
    }

    [Fact]
    public async Task Rsvp_success_records_response()
    {
        var invite = TestData.Invite(Guid.NewGuid(), Guid.NewGuid());
        _invites.GetByTokenHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(invite);

        var res = await Sut().RsvpAsync("tok", Ip1, new RsvpRequest("Going", 2, null, null, null, null));

        Assert.Equal("Going", res.Rsvp);
        Assert.Equal(RsvpStatus.Going, invite.RsvpStatus);
        await _rsvp.Received(1).AddAsync(Arg.Any<RsvpResponse>(), Arg.Any<CancellationToken>());
        // Two separate persists: trusting the first-ever IP, then recording the RSVP.
        await _uow.Received(2).SaveChangesAsync(Arg.Any<CancellationToken>());
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
        _inviters.Query().Returns(Array.Empty<Inviter>().AsAsyncQueryable());

        var cards = await Sut().GetInboxAsync();

        var card = Assert.Single(cards);
        Assert.Equal(campaign.Title, card.EventTitle);
        Assert.Equal("Going", card.RsvpStatus);
    }

    /// <summary>
    /// A merged account answers to BOTH its identifiers, so an invitation sent to the phone and one
    /// sent to the email belong in the same inbox — even though the token names only one of them.
    /// </summary>
    [Fact]
    public async Task Inbox_for_signed_in_account_matches_both_identifiers()
    {
        var campaign = TestData.Campaign();
        var byEmail = TestData.Guest(campaign.Id, email: "me@test.com", phone: null);
        var byPhone = TestData.Guest(campaign.Id, email: null, phone: "+9607771234");
        var stranger = TestData.Guest(campaign.Id, email: "someone@else.com", phone: null);
        var accountId = Guid.NewGuid();

        _currentUser.UserId.Returns(accountId);
        _currentUser.Contact.Returns("me@test.com");   // the token names only the email
        _currentUser.ContactType.Returns("email");
        _users.GetByIdAsync(accountId, Arg.Any<CancellationToken>()).Returns(new AppUser
        {
            Id = accountId, Email = "me@test.com", PhoneE164 = "+9607771234", DisplayName = "Me",
        });

        _guests.Query().Returns(new[] { byEmail, byPhone, stranger }.AsAsyncQueryable());
        _invites.Query().Returns(new[]
        {
            TestData.Invite(campaign.Id, byEmail.Id),
            TestData.Invite(campaign.Id, byPhone.Id),
            TestData.Invite(campaign.Id, stranger.Id),
        }.AsAsyncQueryable());
        _campaigns.Query().Returns(new[] { campaign }.AsAsyncQueryable());
        _inviters.Query().Returns(Array.Empty<Inviter>().AsAsyncQueryable());

        var cards = await Sut().GetInboxAsync();

        Assert.Equal(2, cards.Count);
    }

    /// <summary>
    /// The point of a verified contact link: someone a host invited by EMAIL signs in with the phone
    /// number a different host had for them, and still finds that invitation. Only the proven link
    /// does this — the guest row pairing alone must not (see ContactLinkServiceTests).
    /// </summary>
    [Fact]
    public async Task Inbox_includes_invitations_for_an_email_proven_to_belong_to_the_signed_in_phone()
    {
        var campaign = TestData.Campaign();
        var invitedByEmail = TestData.Guest(campaign.Id, email: "me@test.com", phone: null);
        var stranger = TestData.Guest(campaign.Id, email: "other@test.com", phone: null);

        // Signed in by phone only — no account, so the token names just the number.
        _currentUser.UserId.Returns((Guid?)null);
        _currentUser.Contact.Returns("+9607771234");
        _currentUser.ContactType.Returns("phone");

        _contactLinks.FirstOrDefaultAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<VerifiedContactLink, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns(new VerifiedContactLink
            {
                Id = Guid.NewGuid(), Email = "me@test.com", PhoneE164 = "+9607771234", VerifiedFrom = "phone"
            });

        _guests.Query().Returns(new[] { invitedByEmail, stranger }.AsAsyncQueryable());
        _invites.Query().Returns(new[]
        {
            TestData.Invite(campaign.Id, invitedByEmail.Id),
            TestData.Invite(campaign.Id, stranger.Id),
        }.AsAsyncQueryable());
        _campaigns.Query().Returns(new[] { campaign }.AsAsyncQueryable());
        _inviters.Query().Returns(Array.Empty<Inviter>().AsAsyncQueryable());

        var cards = await Sut().GetInboxAsync();

        Assert.Single(cards);
    }

    /// <summary>Without a proven link, the same phone-only sign-in sees nothing.</summary>
    [Fact]
    public async Task Inbox_ignores_an_email_that_only_shares_a_guest_row_with_the_phone()
    {
        var campaign = TestData.Campaign();
        var invitedByEmail = TestData.Guest(campaign.Id, email: "me@test.com", phone: null);

        _currentUser.UserId.Returns((Guid?)null);
        _currentUser.Contact.Returns("+9607771234");
        _currentUser.ContactType.Returns("phone");
        _contactLinks.FirstOrDefaultAsync(
                Arg.Any<System.Linq.Expressions.Expression<Func<VerifiedContactLink, bool>>>(),
                Arg.Any<CancellationToken>())
            .Returns((VerifiedContactLink?)null);

        _guests.Query().Returns(new[] { invitedByEmail }.AsAsyncQueryable());
        _invites.Query().Returns(new[] { TestData.Invite(campaign.Id, invitedByEmail.Id) }.AsAsyncQueryable());
        _campaigns.Query().Returns(new[] { campaign }.AsAsyncQueryable());
        _inviters.Query().Returns(Array.Empty<Inviter>().AsAsyncQueryable());

        Assert.Empty(await Sut().GetInboxAsync());
    }

    /// <summary>
    /// The inbox lists an invitation matched on EITHER identifier, so answering it must accept
    /// either too — otherwise a merged account sees an invitation it is told it cannot reply to.
    /// </summary>
    [Fact]
    public async Task Rsvp_by_id_accepts_the_accounts_other_identifier()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id, email: "me@test.com", phone: null);
        var invite = TestData.Invite(campaign.Id, guest.Id);
        var accountId = Guid.NewGuid();

        // Signed in by PHONE; the invitation was addressed to the account's email.
        _currentUser.UserId.Returns(accountId);
        _currentUser.Contact.Returns("+9607771234");
        _currentUser.ContactType.Returns("phone");
        _users.GetByIdAsync(accountId, Arg.Any<CancellationToken>()).Returns(new AppUser
        {
            Id = accountId, Email = "me@test.com", PhoneE164 = "+9607771234", DisplayName = "Me",
        });
        _invites.GetByIdAsync(invite.Id, Arg.Any<CancellationToken>()).Returns(invite);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        _rsvp.Query(Arg.Any<bool>()).Returns(Array.Empty<RsvpResponse>().AsAsyncQueryable());

        var result = await Sut().RsvpByInviteIdAsync(invite.Id, new RsvpRequest("Going", null, null, null, null, null));

        Assert.Equal("Going", result.Rsvp);
    }

    [Fact]
    public async Task Rsvp_by_id_still_refuses_someone_elses_invite()
    {
        var campaign = TestData.Campaign();
        var guest = TestData.Guest(campaign.Id, email: "someone@else.com", phone: null);
        var invite = TestData.Invite(campaign.Id, guest.Id);
        var accountId = Guid.NewGuid();

        _currentUser.UserId.Returns(accountId);
        _currentUser.Contact.Returns("+9607771234");
        _currentUser.ContactType.Returns("phone");
        _users.GetByIdAsync(accountId, Arg.Any<CancellationToken>()).Returns(new AppUser
        {
            Id = accountId, Email = "me@test.com", PhoneE164 = "+9607771234", DisplayName = "Me",
        });
        _invites.GetByIdAsync(invite.Id, Arg.Any<CancellationToken>()).Returns(invite);
        _guests.GetByIdAsync(guest.Id, Arg.Any<CancellationToken>()).Returns(guest);

        await Assert.ThrowsAsync<InviteNotFoundException>(() =>
            Sut().RsvpByInviteIdAsync(invite.Id, new RsvpRequest("Going", null, null, null, null, null)));
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
