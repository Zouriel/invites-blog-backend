using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Portfolios;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>"These are mine": the uploader names in a Studio's event, claimed as the studio's own (docs/PORTFOLIO-HANDOFF.md).</summary>
[ApiController]
[Route("api/me/campaigns/{campaignId:guid}/portfolio-uploaders")]
public sealed class PortfolioClaimsController(IPortfolioClaimService claims) : BaseApiController
{
    [HttpGet]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> List(Guid campaignId, CancellationToken ct) =>
        Success(await claims.UploadersAsync(campaignId, ct));

    [HttpPost("claim")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> Claim(Guid campaignId, [FromBody] PortfolioClaimRequest req, CancellationToken ct) =>
        Success(await claims.ClaimAsync(campaignId, req.Key, ct));

    [HttpPost("unclaim")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> Unclaim(Guid campaignId, [FromBody] PortfolioClaimRequest req, CancellationToken ct) =>
        Success(await claims.UnclaimAsync(campaignId, req.Key, ct));
}
