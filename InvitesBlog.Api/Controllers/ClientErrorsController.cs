using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

/// <summary>A failure the camera page caught on someone's phone. Short, and nothing they typed.</summary>
public sealed record ClientErrorReport(string? Where, string? Message, string? Stack);

/// <summary>
/// Where the camera tells us it failed. It runs on phones we never hold, and catches everything so
/// it keeps working, which made its failures invisible: an iPhone dropped every photo and nothing
/// anywhere said so. Logged and nothing else; small bodies only, and cut short.
/// </summary>
[Route("api/client-errors")]
[AllowAnonymous]
public sealed class ClientErrorsController(ILogger<ClientErrorsController> logger) : ControllerBase
{
    [HttpPost]
    [RequestSizeLimit(4096)]
    [EnableRateLimiting("client-errors")]
    public IActionResult Report([FromBody] ClientErrorReport? report)
    {
        static string Cut(string? s, int n) => string.IsNullOrEmpty(s) ? "-" : s.Length > n ? s[..n] : s;
        var agent = Request.Headers.UserAgent.ToString();
        logger.LogWarning("Camera error at {Where}: {Message} | {Agent} | {Stack}",
            Cut(report?.Where, 60), Cut(report?.Message, 300), Cut(agent, 160),
            Cut(report?.Stack, 800).Replace('\n', ' '));
        return NoContent();
    }
}
