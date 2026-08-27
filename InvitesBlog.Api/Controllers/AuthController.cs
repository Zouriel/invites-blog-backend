using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Accounts;
using InvitesBlog.Application.Services.Accounts;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// The single sign-in surface. Staff, designers and customers all arrive here; the roles on the
/// issued token decide what the app shows them afterwards.
/// </summary>
[Route("api/auth")]
public sealed class AuthController(IAccountService accounts) : BaseApiController
{
    /// <summary>What the sign-in page can offer — used to hide options this server can't deliver.</summary>
    [HttpGet("options")]
    [AllowAnonymous]
    public IActionResult Options() => Success(accounts.Options());

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<IActionResult> Login([FromBody] PasswordLoginRequest request, CancellationToken ct) =>
        Success(await accounts.LoginWithPasswordAsync(request, ct));

    /// <summary>
    /// Designer sign-up. Rate-limited like the code endpoints: it is anonymous and it creates
    /// accounts, so it is the obvious thing to hammer.
    /// </summary>
    [HttpPost("register/designer")]
    [AllowAnonymous]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> RegisterDesigner(
        [FromBody] RegisterDesignerRequest request, CancellationToken ct) =>
        Created(await accounts.RegisterDesignerAsync(request, ct));

    /// <summary>Completes an OAuth sign-in started in the browser.</summary>
    [HttpPost("oauth/{provider}")]
    [AllowAnonymous]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> OAuth(
        string provider, [FromBody] OAuthLoginRequest request, CancellationToken ct) =>
        Success(await accounts.OAuthAsync(provider, request, ct));

    /// <summary>Sends a sign-in code to a phone number or an email address.</summary>
    [HttpPost("code/request")]
    [AllowAnonymous]
    public async Task<IActionResult> RequestCode([FromBody] RequestCodeRequest request, CancellationToken ct) =>
        Success(await accounts.RequestCodeAsync(request, ct));

    [HttpPost("code/verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyCode([FromBody] VerifyCodeRequest request, CancellationToken ct) =>
        Success(await accounts.VerifyCodeAsync(request, ct));

    /// <summary>
    /// Step one of a customer sign-up: send a code to the address being claimed. Rate-limited with
    /// the other code-senders, since it is an anonymous endpoint that causes mail to be sent.
    /// </summary>
    [HttpPost("signup/start")]
    [AllowAnonymous]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> StartSignUp([FromBody] RequestCodeRequest request, CancellationToken ct) =>
        Success(await accounts.RequestCodeAsync(request, ct));

    /// <summary>
    /// Step two: the code proves the address, the password is what they sign in with afterwards.
    /// </summary>
    [HttpPost("signup")]
    [AllowAnonymous]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> SignUp([FromBody] SignUpRequest request, CancellationToken ct) =>
        Success(await accounts.SignUpAsync(request, ct));

    [HttpGet("me")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> Me(CancellationToken ct) => Success(await accounts.MeAsync(ct));

    /// <summary>
    /// The account's light/dark preference. Stored on the account, not in the browser, so it follows
    /// the person rather than the device they happened to set it on.
    /// </summary>
    [HttpPut("me/theme")]
    // Same "signed in at all" gate its siblings use — the controller is AllowAnonymous by default.
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> SetTheme([FromBody] SetThemeRequest req, CancellationToken ct) =>
        Success(await accounts.SetThemeAsync(req, ct));

    /// <summary>
    /// Opt the signed-in account into publishing templates. Gated on being signed in at all, not on
    /// any designer permission — asking for the role is exactly what someone who lacks it does.
    /// </summary>
    [HttpPost("me/become-designer")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> BecomeDesigner(CancellationToken ct) =>
        Success(await accounts.BecomeDesignerAsync(ct));

    // ----- Linking a second identifier onto the signed-in account -------------------------------

    [HttpPost("link/request")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> RequestLink([FromBody] RequestCodeRequest request, CancellationToken ct) =>
        Success(await accounts.RequestLinkCodeAsync(request, ct));

    [HttpPost("link/verify")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> VerifyLink([FromBody] VerifyCodeRequest request, CancellationToken ct) =>
        Success(await accounts.VerifyLinkAsync(request, ct));

    // ----- The signed-in person's own history ---------------------------------------------------

    [HttpGet("/api/me/campaigns")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> MyCampaigns(CancellationToken ct) =>
        Success(await accounts.MyCampaignsAsync(ct));

    [HttpGet("/api/me/requests")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> MyRequests(CancellationToken ct) =>
        Success(await accounts.MyRequestsAsync(ct));
}
