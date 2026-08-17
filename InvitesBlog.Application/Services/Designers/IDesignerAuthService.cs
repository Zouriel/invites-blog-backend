using InvitesBlog.Application.Dtos.Designers;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>
/// The signed-in designer's own profile. Signing up, signing in and OAuth linking all live in
/// <c>AccountService</c> — one account, one token, every role it holds.
/// </summary>
public interface IDesignerAuthService
{
    /// <summary>The signed-in designer's own profile, with the providers their account is linked to.</summary>
    Task<DesignerDto> MeAsync(CancellationToken ct = default);
}
