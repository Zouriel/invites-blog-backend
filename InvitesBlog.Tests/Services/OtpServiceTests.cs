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
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IOtpSender _emailSender = Substitute.For<IOtpSender>();
    private readonly IInviteeTokenIssuer _tokenIssuer = Substitute.For<IInviteeTokenIssuer>();
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();
    private IValidator<SendOtpRequest> _sendValidator = TestData.PassingValidator<SendOtpRequest>();
    private IValidator<VerifyOtpRequest> _verifyValidator = TestData.PassingValidator<VerifyOtpRequest>();

    public OtpServiceTests() => _emailSender.Channel.Returns("email");

    private OtpService Sut() => new(
        _challenges, _uow, new[] { _emailSender }, _tokenIssuer,
        new PhoneNormalizer(), _config, _sendValidator, _verifyValidator);

    [Fact]
    public async Task Request_validation_failure_throws_ValidationException()
    {
        _sendValidator = TestData.FailingValidator<SendOtpRequest>();
        await Assert.ThrowsAsync<ValidationException>(
            () => Sut().RequestAsync(new SendOtpRequest("email", null, "a@test.com", null)));
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
        _challenges.CountRecentSendsAsync(null, "user@test.com", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
            .Returns(3);
        var req = new SendOtpRequest("email", null, "user@test.com", null);
        await Assert.ThrowsAsync<OtpRateLimitException>(() => Sut().RequestAsync(req));
    }

    [Fact]
    public async Task Request_success_persists_challenge_and_sends_code()
    {
        _challenges.CountRecentSendsAsync(null, "user@test.com", Arg.Any<DateTimeOffset>(), Arg.Any<CancellationToken>())
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
