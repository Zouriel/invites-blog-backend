using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Otp;
using InvitesBlog.Application.Exceptions.Otp;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Services.Otp;

/// <summary>
/// OTP business logic (§10.7 / §11.1 rules: 6-digit code, configurable expiry, max 5 attempts,
/// max 3 sends per contact per hour). Coordinates the challenge repository, senders and token issuer.
/// </summary>
public sealed class OtpService(
    IOtpChallengeRepository challenges,
    IUnitOfWork uow,
    IEnumerable<IOtpSender> senders,
    IInviteeTokenIssuer tokenIssuer,
    PhoneNormalizer phones,
    IConfiguration config,
    IValidator<SendOtpRequest> sendValidator,
    IValidator<VerifyOtpRequest> verifyValidator) : IOtpService
{
    private const int MaxAttempts = 5;
    private const int MaxSendsPerHour = 3;

    public async Task<OtpChallengeResponse> RequestAsync(SendOtpRequest req, CancellationToken ct = default)
    {
        await sendValidator.ValidateAndThrowAsync(req, ct);

        var channel = req.Channel.Equals("email", StringComparison.OrdinalIgnoreCase)
            ? OtpChannel.Email : OtpChannel.Sms;

        // Email-only at launch; Telegram/SMS ship later without schema changes (provider guide §1/§4).
        var configured = config["Otp:Channels"];
        var enabled = (string.IsNullOrWhiteSpace(configured) ? "Email" : configured)
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (!enabled.Any(c => c.Equals(channel.ToString(), StringComparison.OrdinalIgnoreCase)))
            throw new OtpChannelUnavailableException(channel.ToString());

        string? phone = null, email = null;
        string recipient;
        if (channel == OtpChannel.Sms)
        {
            var norm = phones.Normalize(req.Phone, req.DefaultCountry ?? "MV");
            if (!norm.IsUsable) throw new OtpInvalidPhoneException();
            phone = norm.E164;
            recipient = phone!;
        }
        else
        {
            email = req.Email!.Trim().ToLowerInvariant();
            recipient = email;
        }

        // Send limits (§11.1): 3 per contact per hour.
        var since = DateTimeOffset.UtcNow.AddHours(-1);
        var recentSends = await challenges.CountRecentSendsAsync(phone, email, since, ct);
        if (recentSends >= MaxSendsPerHour) throw new OtpRateLimitException();

        var expiryMinutes = int.TryParse(config["Otp:ExpiryMinutes"], out var m) ? m : 5;
        var code = TokenService.GenerateNumericCode(6);
        var challenge = new OtpChallenge
        {
            Id = Guid.NewGuid(),
            Channel = channel,
            PhoneE164 = phone,
            Email = email,
            CodeHash = TokenService.Hash(code),
            Attempts = 0,
            ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(expiryMinutes),
            CreatedAt = DateTimeOffset.UtcNow
        };
        await challenges.AddAsync(challenge, ct);
        await uow.SaveChangesAsync(ct);

        var sender = senders.First(s => s.Channel.Equals(
            channel == OtpChannel.Sms ? "sms" : "email", StringComparison.OrdinalIgnoreCase));
        await sender.SendCodeAsync(recipient, code, ct);

        return new OtpChallengeResponse(challenge.Id, expiryMinutes * 60);
    }

    public async Task<OtpTokensResponse> VerifyAsync(VerifyOtpRequest req, CancellationToken ct = default)
    {
        await verifyValidator.ValidateAndThrowAsync(req, ct);

        var challenge = await challenges.GetByIdAsync(req.ChallengeId, ct)
            ?? throw new OtpChallengeNotFoundException();
        if (challenge.VerifiedAt is not null) throw new OtpAlreadyUsedException();
        if (challenge.ExpiresAt < DateTimeOffset.UtcNow) throw new OtpExpiredException();
        if (challenge.Attempts >= MaxAttempts) throw new OtpTooManyAttemptsException();

        challenge.Attempts++;
        if (!TokenService.Verify(req.Code, challenge.CodeHash))
        {
            await uow.SaveChangesAsync(ct);
            throw new OtpInvalidCodeException(Math.Max(0, MaxAttempts - challenge.Attempts));
        }

        challenge.VerifiedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);

        var contactType = challenge.Channel == OtpChannel.Sms ? "phone" : "email";
        var contact = challenge.Channel == OtpChannel.Sms ? challenge.PhoneE164! : challenge.Email!;
        var accessToken = tokenIssuer.Issue(contactType, contact, TimeSpan.FromDays(30));
        return new OtpTokensResponse(accessToken, TokenService.GenerateToken());
    }
}
