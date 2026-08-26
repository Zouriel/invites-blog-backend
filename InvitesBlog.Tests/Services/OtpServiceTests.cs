using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Otp;
using InvitesBlog.Application.Exceptions.Otp;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Security;
using InvitesBlog.Application.Services.Otp;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class OtpServiceTests
{
    private readonly IOtpChallengeRepository _challenges = Substitute.For<IOtpChallengeRepository>();
    private readonly IGuestRepository _guests = Substitute.For<IGuestRepository>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IOtpSender _emailSender = Substitute.For<IOtpSender>();
    private readonly IOtpSender _smsSender = Substitute.For<IOtpSender>();
    private readonly IInviteeTokenIssuer _tokenIssuer = Substitute.For<IInviteeTokenIssuer>();
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private IValidator<SendOtpRequest> _sendValidator = TestData.PassingValidator<SendOtpRequest>();
    private IValidator<VerifyOtpRequest> _verifyValidator = TestData.PassingValidator<VerifyOtpRequest>();

    public OtpServiceTests()
    {
        _emailSender.Channel.Returns("email");
        _smsSender.Channel.Returns("sms");
        _config["Otp:Channels"].Returns("Email,Sms"); // both channels enabled for these unit tests
    }

    private OtpService Sut() => new(
        _challenges, _guests, _campaigns, _uow, new[] { _emailSender, _smsSender }, _tokenIssuer,
        new PhoneNormalizer(), _config, _sendValidator, _verifyValidator);

    [Fact]
    public async Task Request_validation_failure_throws_ValidationException()
    {
        _sendValidator = TestData.FailingValidator<SendOtpRequest>();
        await Assert.ThrowsAsync<ValidationException>(
            () => Sut().RequestAsync(new SendOtpRequest("email", null, "a@test.com", null)));
    }

    [Fact]
    public async Task Request_sms_when_only_email_enabled_throws_channel_unavailable()
    {
        _config["Otp:Channels"].Returns("Email"); // launch config — email only
        var req = new SendOtpRequest("sms", "7777777", null, "MV");
        await Assert.ThrowsAsync<OtpChannelUnavailableException>(() => Sut().RequestAsync(req));
    }

    [Fact]
    public async Task Request_sms_invalid_phone_throws()
    {
        var req = new SendOtpRequest("sms", "not-a-number", null, "MV");
        await Assert.ThrowsAsync<OtpInvalidPhoneException>(() => Sut().RequestAsync(req));
    }

    [Fact]
    public async Task Request_over_send_limit_throws_RateLimit()
    {
        _challenges.CountRecentSendsAsync(null, "user@test.com", Arg.Any<OtpPurpose>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(3);
        var req = new SendOtpRequest("email", null, "user@test.com", null);
        await Assert.ThrowsAsync<OtpRateLimitException>(() => Sut().RequestAsync(req));
    }

    [Fact]
    public async Task A_spent_reauth_budget_does_not_stop_the_same_person_signing_in()
    {
        // The lockout this split exists to prevent. Reauth fires whenever a personal invite link is
        // opened from an unrecognised IP — which a phone does just by moving between wifi and mobile
        // data. When both flows shared one allowance, three such opens left the guest unable to sign
        // in at all for an hour.
        _challenges.CountRecentSendsAsync(null, "user@test.com", OtpPurpose.InviteReauth,
                Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(3);
        _challenges.CountRecentSendsAsync(null, "user@test.com", OtpPurpose.SignIn,
                Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(0);

        var res = await Sut().RequestAsync(new SendOtpRequest("email", null, "user@test.com", null));

        Assert.True(res.ExpiresInSeconds > 0);
    }

    [Fact]
    public async Task Each_budget_still_has_its_own_ceiling()
    {
        // Splitting them must not uncap either one: a leaked link should not be able to mail-bomb a
        // guest just because sign-in codes are counted elsewhere.
        _challenges.CountRecentSendsAsync(null, "user@test.com", OtpPurpose.InviteReauth,
                Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(3);

        await Assert.ThrowsAsync<OtpRateLimitException>(() => Sut().RequestAsync(
            new SendOtpRequest("email", null, "user@test.com", null), OtpPurpose.InviteReauth));
    }

    [Fact]
    public async Task A_code_records_which_budget_it_came_from()
    {
        OtpChallenge? saved = null;
        await _challenges.AddAsync(Arg.Do<OtpChallenge>(c => saved = c), Arg.Any<CancellationToken>());
        _challenges.CountRecentSendsAsync(null, "user@test.com", Arg.Any<OtpPurpose>(),
                Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(0);

        await Sut().RequestAsync(
            new SendOtpRequest("email", null, "user@test.com", null), OtpPurpose.InviteReauth);

        Assert.Equal(OtpPurpose.InviteReauth, saved!.Purpose);
    }

    [Fact]
    public async Task Request_success_persists_challenge_and_sends_code()
    {
        _challenges.CountRecentSendsAsync(null, "user@test.com", Arg.Any<OtpPurpose>(), Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(0);
        var req = new SendOtpRequest("email", null, "user@test.com", null);

        var res = await Sut().RequestAsync(req);

        Assert.Equal(5 * 60, res.ExpiresInSeconds); // default 5 minutes
        await _challenges.Received(1).AddAsync(Arg.Any<OtpChallenge>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
        await _emailSender.Received(1).SendCodeAsync("user@test.com", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Verify_unknown_challenge_throws_NotFound()
    {
        _challenges.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((OtpChallenge?)null);
        await Assert.ThrowsAsync<OtpChallengeNotFoundException>(
            () => Sut().VerifyAsync(new VerifyOtpRequest(Guid.NewGuid(), "123456")));
    }

    [Fact]
    public async Task Verify_already_used_throws()
    {
        var c = Challenge();
        c.VerifiedAt = DateTimeOffset.UtcNow;
        _challenges.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
        await Assert.ThrowsAsync<OtpAlreadyUsedException>(
            () => Sut().VerifyAsync(new VerifyOtpRequest(c.Id, "123456")));
    }

    [Fact]
    public async Task Verify_expired_throws()
    {
        var c = Challenge();
        c.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        _challenges.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
        await Assert.ThrowsAsync<OtpExpiredException>(
            () => Sut().VerifyAsync(new VerifyOtpRequest(c.Id, "123456")));
    }

    [Fact]
    public async Task Verify_too_many_attempts_throws()
    {
        var c = Challenge();
        c.Attempts = 5;
        _challenges.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
        await Assert.ThrowsAsync<OtpTooManyAttemptsException>(
            () => Sut().VerifyAsync(new VerifyOtpRequest(c.Id, "123456")));
    }

    [Fact]
    public async Task Verify_wrong_code_increments_attempts_and_throws_InvalidCode()
    {
        var c = Challenge();
        _challenges.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);

        await Assert.ThrowsAsync<OtpInvalidCodeException>(
            () => Sut().VerifyAsync(new VerifyOtpRequest(c.Id, "000000")));

        Assert.Equal(1, c.Attempts);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Verify_success_marks_used_and_issues_token()
    {
        var c = Challenge();
        _challenges.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
        _tokenIssuer.Issue("email", "verify@test.com", Arg.Any<TimeSpan>()).Returns("jwt-token");

        var res = await Sut().VerifyAsync(new VerifyOtpRequest(c.Id, "123456"));

        Assert.Equal("jwt-token", res.AccessToken);
        Assert.NotNull(c.VerifiedAt);
        _tokenIssuer.Received(1).Issue("email", "verify@test.com", Arg.Any<TimeSpan>());
    }

    // ----- Guest-list-gated campaign OTP -----

    [Fact]
    public async Task RequestForCampaign_email_not_on_guest_list_returns_not_invited_and_sends_nothing()
    {
        var c = TestData.Campaign();
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
        _guests.ListByCampaignAsync(c.Id, false, Arg.Any<CancellationToken>())
            .Returns(new[] { TestData.Guest(c.Id, email: "invited@test.com") });

        var res = await Sut().RequestForCampaignAsync(c.Id, new CampaignOtpRequest("stranger@test.com"));

        Assert.False(res.Invited);
        Assert.False(res.Cancelled);
        Assert.Null(res.ChallengeId);
        await _challenges.DidNotReceive().AddAsync(Arg.Any<OtpChallenge>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceive().SendCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestForCampaign_email_on_guest_list_creates_challenge_and_sends_code()
    {
        var c = TestData.Campaign();
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
        _guests.ListByCampaignAsync(c.Id, false, Arg.Any<CancellationToken>())
            .Returns(new[] { TestData.Guest(c.Id, email: "invited@test.com") });

        // Match is case-insensitive and trimmed.
        var res = await Sut().RequestForCampaignAsync(c.Id, new CampaignOtpRequest("  Invited@Test.com "));

        Assert.True(res.Invited);
        Assert.NotNull(res.ChallengeId);
        await _challenges.Received(1).AddAsync(Arg.Any<OtpChallenge>(), Arg.Any<CancellationToken>());
        await _emailSender.Received(1).SendCodeAsync("invited@test.com", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestForCampaign_phone_on_guest_list_texts_the_code()
    {
        // A host who listed a guest by number only: that guest can still prove they belong on this
        // campaign, which is the whole point of allowing a number at the invite gate.
        var c = TestData.Campaign();
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
        _guests.ListByCampaignAsync(c.Id, false, Arg.Any<CancellationToken>())
            .Returns(new[] { TestData.Guest(c.Id, email: null, phone: "+9607819157") });

        // Local format normalises against the default country before the guest-list check.
        var res = await Sut().RequestForCampaignAsync(c.Id, new CampaignOtpRequest(null, "7819157", "MV"));

        Assert.True(res.Invited);
        Assert.NotNull(res.ChallengeId);
        await _smsSender.Received(1).SendCodeAsync("+9607819157", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceive().SendCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestForCampaign_phone_not_on_guest_list_sends_nothing()
    {
        var c = TestData.Campaign();
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
        _guests.ListByCampaignAsync(c.Id, false, Arg.Any<CancellationToken>())
            .Returns(new[] { TestData.Guest(c.Id, email: null, phone: "+9607819157") });

        var res = await Sut().RequestForCampaignAsync(c.Id, new CampaignOtpRequest(null, "7770000", "MV"));

        Assert.False(res.Invited);
        Assert.Null(res.ChallengeId);
        await _smsSender.DidNotReceive().SendCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestForCampaign_unusable_phone_sends_nothing()
    {
        var c = TestData.Campaign();
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
        _guests.ListByCampaignAsync(c.Id, false, Arg.Any<CancellationToken>())
            .Returns(new[] { TestData.Guest(c.Id, email: null, phone: "+9607819157") });

        var res = await Sut().RequestForCampaignAsync(c.Id, new CampaignOtpRequest(null, "12", "MV"));

        Assert.False(res.Invited);
        await _smsSender.DidNotReceive().SendCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestForCampaign_phone_wins_when_both_contacts_are_supplied()
    {
        // The caller chose a channel; quietly mailing instead would put the code somewhere they may
        // not be watching.
        var c = TestData.Campaign();
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
        _guests.ListByCampaignAsync(c.Id, false, Arg.Any<CancellationToken>())
            .Returns(new[] { TestData.Guest(c.Id, email: "invited@test.com", phone: "+9607819157") });

        var res = await Sut().RequestForCampaignAsync(
            c.Id, new CampaignOtpRequest("invited@test.com", "7819157", "MV"));

        Assert.True(res.Invited);
        await _smsSender.Received(1).SendCodeAsync("+9607819157", Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _emailSender.DidNotReceive().SendCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestForCampaign_cancelled_campaign_returns_cancelled_without_sending()
    {
        var c = TestData.Campaign(status: CampaignStatus.Cancelled);
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);

        var res = await Sut().RequestForCampaignAsync(c.Id, new CampaignOtpRequest("invited@test.com"));

        Assert.False(res.Invited);
        Assert.True(res.Cancelled);
        await _emailSender.DidNotReceive().SendCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RequestForCampaign_unknown_campaign_returns_not_invited_without_leaking()
    {
        _campaigns.GetByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Campaign?)null);

        var res = await Sut().RequestForCampaignAsync(Guid.NewGuid(), new CampaignOtpRequest("anyone@test.com"));

        Assert.False(res.Invited);
        Assert.False(res.Cancelled);
        await _emailSender.DidNotReceive().SendCodeAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static OtpChallenge Challenge() => new()
    {
        Id = Guid.NewGuid(),
        Channel = OtpChannel.Email,
        Email = "verify@test.com",
        CodeHash = TokenService.Hash("123456"),
        Attempts = 0,
        ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
        CreatedAt = DateTimeOffset.UtcNow
    };
}
