using FluentValidation;
using InvitesBlog.Application.Dtos.Invites;

namespace InvitesBlog.Application.Validation.Invites;

/// <summary>Presence rules for a zero-login RSVP (§4.9.6). The status string is mapped to
/// <c>RsvpStatus</c> in the service.</summary>
public sealed class RsvpRequestValidator : AbstractValidator<RsvpRequest>
{
    public RsvpRequestValidator()
    {
        RuleFor(x => x.Status).NotEmpty();
        RuleFor(x => x.GuestCount).GreaterThan(0).When(x => x.GuestCount.HasValue);
    }
}
