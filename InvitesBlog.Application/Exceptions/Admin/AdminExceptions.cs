namespace InvitesBlog.Application.Exceptions.Admin;

/// <summary>Admin login failed — unknown user, wrong password, disabled account, or non-admin.</summary>
public sealed class AdminLoginFailedException()
    : UnauthorizedException("Invalid email or password.", "admin_login_failed");
