namespace InvitesBlog.Application.Exceptions.Otp;

public sealed class OtpChallengeNotFoundException()
    : NotFoundException("The verification challenge was not found.", "otp_not_found");

public sealed class OtpExpiredException()
    : InvalidStateException("The verification code has expired.", "otp_expired");

public sealed class OtpAlreadyUsedException()
    : InvalidStateException("The verification code was already used.", "otp_used");

public sealed class OtpInvalidCodeException(int attemptsLeft)
    : BusinessRuleException($"Invalid code. {attemptsLeft} attempt(s) left.", "otp_invalid");

public sealed class OtpTooManyAttemptsException()
    : InvalidStateException("Too many attempts. Request a new code.", "otp_too_many_attempts");

public sealed class OtpRateLimitException()
    : InvalidStateException("Too many codes requested. Try again later.", "otp_rate_limited");

/// <summary>The requested OTP channel isn't enabled at launch (email-only; §Provider guide §1/§4).</summary>
public sealed class OtpChannelUnavailableException(string channel)
    : BusinessRuleException(
        channel.Equals("Sms", StringComparison.OrdinalIgnoreCase)
            ? "Phone sign-in isn't available yet — verify with your email address instead. Your invite links always work without signing in."
            : $"The '{channel}' verification channel isn't available.",
        "otp_channel_unavailable");
