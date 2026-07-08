using FluentValidation;
using InvitesBlog.Application.Dtos.Campaigns;

namespace InvitesBlog.Application.Validation.Campaigns;

public sealed class UpdateInviterRequestValidator : AbstractValidator<UpdateInviterRequest>
{
    public UpdateInviterRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        // Phone is optional — the platform is email-first (email-only OTP at launch), so a host
        // need not supply a phone. It's normalized when present.
    }
}
