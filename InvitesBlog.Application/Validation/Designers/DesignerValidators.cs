using FluentValidation;
using InvitesBlog.Application.Dtos.Designers;

namespace InvitesBlog.Application.Validation.Designers;

public sealed class DesignerRegisterRequestValidator : AbstractValidator<DesignerRegisterRequest>
{
    public DesignerRegisterRequestValidator()
    {
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(200);
        RuleFor(x => x.DisplayName).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Password)
            .NotEmpty()
            .MinimumLength(10).WithMessage("Use at least 10 characters.")
            .MaximumLength(200);
    }
}
