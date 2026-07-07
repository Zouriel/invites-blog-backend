namespace InvitesBlog.Application.Exceptions.Privacy;

/// <summary>The removal token did not resolve to an invite / guest / campaign (§15.3).</summary>
public sealed class PrivacyInviteNotFoundException()
    : NotFoundException("The removal link is invalid or has expired.", "privacy_invite_not_found");
