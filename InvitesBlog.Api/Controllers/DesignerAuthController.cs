using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Infrastructure.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// Designer sign-up / sign-in. Every action here is anonymous by definition — the caller has no token
/// yet — except <c>me</c>, which reads back the account the issued token belongs to.
/// </summary>
[Route("api/designer/auth")]
public sealed class DesignerAuthController(IDesignerAuthService auth) : BaseApiController
{
    /// <summary>Which OAuth buttons the sign-in page should show.</summary>
    [HttpGet("providers")]
    [AllowAnonymous]
    public IActionResult Providers() => Success(auth.ConfiguredProviders());

    [HttpPost("register")]
    [AllowAnonymous]
    public async Task<IActionResult> Register([FromBody] DesignerRegisterRequest request, CancellationToken ct) =>
        Success(await auth.RegisterAsync(request, ct));

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] DesignerLoginRequest request, CancellationToken ct) =>
        Success(await auth.LoginAsync(request, ct));

    [HttpPost("oauth/google")]
    [AllowAnonymous]
    public async Task<IActionResult> Google([FromBody] DesignerOAuthRequest request, CancellationToken ct) =>
        Success(await auth.OAuthAsync(GoogleAuthProvider.Key, request, ct));

    [HttpPost("oauth/microsoft")]
    [AllowAnonymous]
    public async Task<IActionResult> Microsoft([FromBody] DesignerOAuthRequest request, CancellationToken ct) =>
        Success(await auth.OAuthAsync(MicrosoftAuthProvider.Key, request, ct));

    [HttpGet("me")]
    [HasPermission(Permissions.Designer.Manage)]
    public async Task<IActionResult> Me(CancellationToken ct) =>
        Success(await auth.MeAsync(ct));
}
