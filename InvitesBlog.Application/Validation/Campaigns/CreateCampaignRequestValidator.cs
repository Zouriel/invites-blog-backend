using FluentValidation;
using InvitesBlog.Application.Dtos.Campaigns;

namespace InvitesBlog.Application.Validation.Campaigns;

public sealed class CreateCampaignRequestValidator : AbstractValidator<CreateCampaignRequest>
{
    public CreateCampaignRequestValidator()
    {
        RuleFor(x => x.TemplateId).NotEmpty();
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
    }
}
