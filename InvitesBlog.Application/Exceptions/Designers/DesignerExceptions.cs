namespace InvitesBlog.Application.Exceptions.Designers;

/// <summary>The account exists but an admin has suspended it.</summary>
public sealed class DesignerSuspendedException()
    : ForbiddenException("This designer account has been suspended. Contact support if you think that's a mistake.",
        "designer_suspended");
