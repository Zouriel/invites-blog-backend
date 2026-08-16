using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Dtos.Designers;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>
/// Designer accounts (§community templates). Backed by the same generic
/// <c>AppUser</c>/<c>Role</c>/<c>UserRole</c> tables as admin login — no parallel auth system.
/// </summary>
public interface IDesignerAuthService
{
    Task<DesignerAuthResultDto> RegisterAsync(DesignerRegisterRequest request, CancellationToken ct = default);
    Task<DesignerAuthResultDto> LoginAsync(DesignerLoginRequest request, CancellationToken ct = default);

    /// <summary>
    /// Signs in with a verified external identity, creating the account on first use and linking the
    /// provider to the existing account when the verified email already has one.
    /// </summary>
    Task<DesignerAuthResultDto> OAuthAsync(string provider, DesignerOAuthRequest request, CancellationToken ct = default);

    /// <summary>The signed-in designer's own profile.</summary>
    Task<DesignerDto> MeAsync(CancellationToken ct = default);

    /// <summary>
    /// The OAuth providers actually configured on this server, so the sign-in page can offer only
    /// buttons that will work rather than ones that fail on click.
    /// </summary>
    IReadOnlyList<ExternalAuthDescriptor> ConfiguredProviders();
}
