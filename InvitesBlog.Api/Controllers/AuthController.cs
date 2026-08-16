using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Accounts;
using InvitesBlog.Application.Services.Accounts;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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

    /// <summary>Sends a sign-in code to a phone number or an email address.</summary>
    [HttpPost("code/request")]
    [AllowAnonymous]
    public async Task<IActionResult> RequestCode([FromBody] RequestCodeRequest request, CancellationToken ct) =>
        Success(await accounts.RequestCodeAsync(request, ct));

    [HttpPost("code/verify")]
    [AllowAnonymous]
    public async Task<IActionResult> VerifyCode([FromBody] VerifyCodeRequest request, CancellationToken ct) =>
        Success(await accounts.VerifyCodeAsync(request, ct));

    [HttpGet("me")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> Me(CancellationToken ct) => Success(await accounts.MeAsync(ct));

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
