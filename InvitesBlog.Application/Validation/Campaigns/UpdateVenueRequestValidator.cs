using FluentValidation;
using InvitesBlog.Application.Dtos.Campaigns;

namespace InvitesBlog.Application.Validation.Campaigns;

public sealed class UpdateVenueRequestValidator : AbstractValidator<UpdateVenueRequest>
{
    public UpdateVenueRequestValidator()
    {
        RuleFor(x => x.VenueType).NotEmpty().MaximumLength(100);
        RuleFor(x => x.VenueName).NotEmpty().MaximumLength(200);
    }
}
