namespace InvitesBlog.Application.Exceptions.Accounts;

/// <summary>Sign-in failed — unknown account, wrong password, or no password set.</summary>
public sealed class SignInFailedException()
    : UnauthorizedException("Invalid email or password.", "sign_in_failed");

/// <summary>The account exists but an admin has suspended it.</summary>
public sealed class AccountSuspendedException()
    : ForbiddenException("This account has been suspended. Contact support if you think that's a mistake.",
        "account_suspended");
