using InvitesBlog.Application.Dtos.Inquiries;
using InvitesBlog.Application.Services.Inquiries;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

/// <summary>Public "Start an inquiry" submission (§custom invitations). Thin — delegates to the service.</summary>
[Route("api/inquiries")]
public sealed class InquiriesController(IInquiryService inquiries) : BaseApiController
{
    [HttpPost]
    [AllowAnonymous]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> Submit([FromBody] SubmitInquiryRequest req, CancellationToken ct) =>
        Created(await inquiries.SubmitAsync(req, ct));
}
