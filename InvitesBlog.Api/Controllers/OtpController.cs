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
}
