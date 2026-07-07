namespace InvitesBlog.Application.Exceptions.Campaigns;

public sealed class CampaignNotFoundException(Guid id)
    : NotFoundException($"Campaign '{id}' was not found.", "campaign_not_found");

public sealed class CampaignAccessDeniedException()
    : ForbiddenException("The campaign access token is missing or invalid.", "campaign_access_denied");

public sealed class CampaignHasNoGuestsException()
    : BusinessRuleException("Add at least one guest before checkout.", "campaign_no_guests");

public sealed class CampaignInvalidStateException(string message)
    : InvalidStateException(message, "campaign_invalid_state");

/// <summary>The dashboard magic-link token is missing or does not match the campaign (§13.3).</summary>
public sealed class InvalidDashboardTokenException()
    : UnauthorizedException("The dashboard link is missing or invalid.", "dashboard_token_invalid");

/// <summary>The referenced template does not exist or is not active (§10.3 create).</summary>
public sealed class UnknownTemplateException()
    : BusinessRuleException("Unknown template.", "campaign_unknown_template");
