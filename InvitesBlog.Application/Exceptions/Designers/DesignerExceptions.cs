namespace InvitesBlog.Application.Exceptions.Designers;

/// <summary>Designer sign-in failed — unknown account, wrong password, suspended, or not a designer.</summary>
public sealed class DesignerLoginFailedException()
    : UnauthorizedException("Invalid email or password.", "designer_login_failed");

/// <summary>That email already has an account.</summary>
public sealed class DesignerEmailTakenException()
    : AlreadyExistsException("An account with that email already exists — sign in instead.", "designer_email_taken");

/// <summary>The account exists but an admin has suspended it.</summary>
public sealed class DesignerSuspendedException()
    : ForbiddenException("This designer account has been suspended. Contact support if you think that's a mistake.",
        "designer_suspended");
