using FluentValidation;
using InvitesBlog.Application.Dtos.Campaigns;

namespace InvitesBlog.Application.Validation.Campaigns;

public sealed class UpdateContentRequestValidator : AbstractValidator<UpdateContentRequest>
{
    public UpdateContentRequestValidator()
    {
        // All fields are optional patches; only enforce ordering when both dates are supplied.
        RuleFor(x => x.EventEndAt)
            .GreaterThanOrEqualTo(x => x.EventStartAt!.Value)
            .When(x => x.EventStartAt.HasValue && x.EventEndAt.HasValue)
            .WithMessage("Event end must be on or after the event start.");
        RuleFor(x => x.EventType).MaximumLength(100).When(x => x.EventType is not null);
    }
}
