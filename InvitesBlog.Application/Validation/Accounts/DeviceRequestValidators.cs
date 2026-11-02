using FluentValidation;
using InvitesBlog.Application.Dtos.Accounts;
using InvitesBlog.Domain.Entities;

namespace InvitesBlog.Application.Validation.Accounts;

public sealed class RegisterDeviceRequestValidator : AbstractValidator<RegisterDeviceRequest>
{
    public RegisterDeviceRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(DeviceToken.MaxTokenLength);
        RuleFor(x => x.Platform)
            .Must(p => p is DeviceToken.Android or DeviceToken.Ios)
            .WithMessage("Platform must be android or ios.");
        RuleFor(x => x.Environment)
            .Must(e => e is null or DeviceToken.Production or DeviceToken.Sandbox)
            .WithMessage("Environment must be production or sandbox.");
    }
}

public sealed class RemoveDeviceRequestValidator : AbstractValidator<RemoveDeviceRequest>
{
    public RemoveDeviceRequestValidator()
    {
        RuleFor(x => x.Token).NotEmpty().MaximumLength(DeviceToken.MaxTokenLength);
    }
}
