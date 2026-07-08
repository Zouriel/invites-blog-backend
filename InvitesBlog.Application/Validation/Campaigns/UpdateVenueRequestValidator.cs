using FluentValidation;
using InvitesBlog.Application.Dtos.Campaigns;

namespace InvitesBlog.Application.Validation.Campaigns;

public sealed class UpdateVenueRequestValidator : AbstractValidator<UpdateVenueRequest>
{
    public UpdateVenueRequestValidator()
    {
        // Venue is an optional wizard step — a guest can be invited without one. Only cap lengths;
        // don't force a type/name, or the step 400s when the inviter leaves details blank.
        RuleFor(x => x.VenueType).MaximumLength(100);
        RuleFor(x => x.VenueName).MaximumLength(200);
    }
}
