using FluentValidation;
using InvitesBlog.Application.Dtos.Campaigns;

namespace InvitesBlog.Application.Validation.Campaigns;

public sealed class UpdateDeliverySettingsRequestValidator : AbstractValidator<UpdateDeliverySettingsRequest>
{
    public UpdateDeliverySettingsRequestValidator()
    {
        RuleFor(x => x.DeliverySettingsJson).NotEmpty();
    }
}
