using FluentValidation;
using InvitesBlog.Application.Dtos.Otp;

namespace InvitesBlog.Application.Validation.Otp;

/// <summary>Presence rules for an OTP verify request.</summary>
public sealed class VerifyOtpRequestValidator : AbstractValidator<VerifyOtpRequest>
{
    public VerifyOtpRequestValidator()
    {
        RuleFor(x => x.ChallengeId).NotEmpty();
        RuleFor(x => x.Code).NotEmpty();
    }
}
