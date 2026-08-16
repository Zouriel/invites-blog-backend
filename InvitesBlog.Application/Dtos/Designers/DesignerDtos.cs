namespace InvitesBlog.Application.Dtos.Designers;

/// <summary>Designer sign-up: email + password, exactly like the admin account pattern.</summary>
public sealed record DesignerRegisterRequest(string Email, string Password, string DisplayName);

/// <summary>Designer sign-in credentials.</summary>
public sealed record DesignerLoginRequest(string Email, string Password);

/// <summary>
/// An OAuth sign-in: the ID token the client received from Google/Microsoft. The server verifies it
/// against the provider's published keys — the client is never trusted to assert who it is.
/// </summary>
public sealed record DesignerOAuthRequest(string IdToken);

/// <summary>The signed-in designer.</summary>
public sealed record DesignerDto(
    Guid Id, string Email, string DisplayName, bool IsActive, IReadOnlyList<string> LinkedProviders);

/// <summary>A successful designer sign-in: the issued Designer JWT plus the account.</summary>
public sealed record DesignerAuthResultDto(string Token, DateTimeOffset ExpiresAt, DesignerDto Designer);
