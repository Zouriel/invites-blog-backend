namespace InvitesBlog.Application.Dtos.Designers;

/// <summary>Designer sign-up: email + password, exactly like the admin account pattern.</summary>
public sealed record DesignerRegisterRequest(string Email, string Password, string DisplayName);

