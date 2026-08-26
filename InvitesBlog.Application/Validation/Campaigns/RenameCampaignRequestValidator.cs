using FluentValidation;
using InvitesBlog.Application.Dtos.Campaigns;

namespace InvitesBlog.Application.Validation.Campaigns;

/// <summary>Same bounds the title is created under — a rename cannot reach a state creation can't.</summary>
public sealed class RenameCampaignRequestValidator : AbstractValidator<RenameCampaignRequest>
{
    public RenameCampaignRequestValidator()
    {
        RuleFor(x => x.Title).NotEmpty().MaximumLength(200);
    }
}
