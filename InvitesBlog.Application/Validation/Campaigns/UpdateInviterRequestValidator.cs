using FluentValidation;
using InvitesBlog.Application.Dtos.Campaigns;

namespace InvitesBlog.Application.Validation.Campaigns;

public sealed class UpdateInviterRequestValidator : AbstractValidator<UpdateInviterRequest>
{
    public UpdateInviterRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(200);
        RuleFor(x => x.Email).NotEmpty().EmailAddress();
        RuleFor(x => x.Phone).NotEmpty();
    }
}
