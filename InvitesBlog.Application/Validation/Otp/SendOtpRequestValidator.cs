using FluentValidation;
using InvitesBlog.Application.Dtos.Otp;

namespace InvitesBlog.Application.Validation.Otp;

/// <summary>Presence rules for an OTP send request (§11.1). Phone shape is validated in the service
/// via <c>PhoneNormalizer</c>.</summary>
public sealed class SendOtpRequestValidator : AbstractValidator<SendOtpRequest>
{
    public SendOtpRequestValidator()
    {
        RuleFor(x => x.Channel).NotEmpty();

        When(x => "email".Equals(x.Channel, StringComparison.OrdinalIgnoreCase), () =>
            RuleFor(x => x.Email).NotEmpty().WithMessage("An email address is required."));

        When(x => !"email".Equals(x.Channel, StringComparison.OrdinalIgnoreCase), () =>
            RuleFor(x => x.Phone).NotEmpty().WithMessage("A phone number is required."));
    }
}
