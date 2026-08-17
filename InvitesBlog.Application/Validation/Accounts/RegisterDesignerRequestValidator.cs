using FluentValidation;
using InvitesBlog.Application.Dtos.Accounts;

namespace InvitesBlog.Application.Validation.Accounts;

/// <summary>
/// The only self-service sign-up on the platform, so the rules are the ones that keep a real person
/// reachable: a valid email, a name to publish under, and a password long enough to be worth having.
/// </summary>
public sealed class RegisterDesignerRequestValidator : AbstractValidator<RegisterDesignerRequest>
{
    public RegisterDesignerRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(200);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(10).WithMessage("Use at least 10 characters.")
            .MaximumLength(200);
    }
}
