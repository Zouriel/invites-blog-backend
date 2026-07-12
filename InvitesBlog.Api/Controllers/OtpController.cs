using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Otp;
using InvitesBlog.Application.Services.Otp;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

/// <summary>§10.7 OTP request/verify. Thin controller — delegates to <see cref="IOtpService"/>.</summary>
[Route("api/otp")]
public sealed class OtpController(IOtpService otp) : BaseApiController
{
    [HttpPost("request")]
    [AllowAnonymous]
    [HasPermission(Permissions.Otp.Request)]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> Request([FromBody] SendOtpRequest req, CancellationToken ct) =>
        Success(await otp.RequestAsync(req, ct));

    [HttpPost("verify")]
    [AllowAnonymous]
    [HasPermission(Permissions.Otp.Verify)]
    public async Task<IActionResult> Verify([FromBody] VerifyOtpRequest req, CancellationToken ct) =>
        Success(await otp.VerifyAsync(req, ct));

    /// <summary>
    /// POST /api/campaigns/{campaignId}/request-otp — guest-list-gated OTP for the shared campaign link.
    /// Only sends a code (and returns a challenge) if the email is on the campaign's guest list, so an
    /// uninvited address is told "not invited" and no email is wasted.
    /// </summary>
    [HttpPost("/api/campaigns/{campaignId:guid}/request-otp")]
    [AllowAnonymous]
    [HasPermission(Permissions.Otp.Request)]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> RequestForCampaign(
        Guid campaignId, [FromBody] CampaignOtpRequest req, CancellationToken ct) =>
        Success(await otp.RequestForCampaignAsync(campaignId, req.Email, ct));
}
