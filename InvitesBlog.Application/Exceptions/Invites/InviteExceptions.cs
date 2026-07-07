namespace InvitesBlog.Application.Exceptions.Invites;

public sealed class InviteNotFoundException()
    : NotFoundException("This invite link is not valid.", "invite_not_found");

public sealed class InviteRequiresOtpException()
    : ForbiddenException("This invite requires verification before viewing.", "invite_requires_otp");

public sealed class InvalidRsvpStatusException(string status)
    : BusinessRuleException($"'{status}' is not a valid RSVP status.", "invalid_rsvp_status");
