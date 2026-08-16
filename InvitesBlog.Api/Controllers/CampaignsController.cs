using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// §10.3 Campaign builder + §4.6 inviter details / no-registration access. Thin controller —
/// delegates to <see cref="ICampaignService"/>; ownership + token validation live in the service.
/// </summary>
[Route("api/campaigns")]
public sealed class CampaignsController(ICampaignService campaigns) : BaseApiController
{
    // Creation is open to anonymous callers: a brand-new visitor spins up a draft and receives a
    // possession token that grants Inviter rights thereafter. The Public role does NOT hold
    // Campaigns.Create, so this action is deliberately [AllowAnonymous] rather than permission-gated.
    [HttpPost]
    [AllowAnonymous]
    public async Task<IActionResult> Create([FromBody] CreateCampaignRequest req, CancellationToken ct) =>
        Created(await campaigns.CreateAsync(req, ct));

    [HttpPut("{id:guid}/content")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> UpdateContent(Guid id, [FromBody] UpdateContentRequest req, CancellationToken ct)
    {
        await campaigns.UpdateContentAsync(id, req, ct);
        return SuccessMessage("Campaign content updated.");
    }

    [HttpPut("{id:guid}/venue")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> UpdateVenue(Guid id, [FromBody] UpdateVenueRequest req, CancellationToken ct)
    {
        await campaigns.UpdateVenueAsync(id, req, ct);
        return SuccessMessage("Venue updated.");
    }

    [HttpPut("{id:guid}/inviter")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> UpdateInviter(Guid id, [FromBody] UpdateInviterRequest req, CancellationToken ct)
    {
        await campaigns.UpdateInviterAsync(id, req, BearerToken(), ct);
        return SuccessMessage("Inviter details saved. A resume link has been emailed.");
    }

    [HttpPut("{id:guid}/delivery-settings")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> UpdateDeliverySettings(Guid id, [FromBody] UpdateDeliverySettingsRequest req, CancellationToken ct)
    {
        await campaigns.UpdateDeliverySettingsAsync(id, req, ct);
        return SuccessMessage("Delivery settings updated.");
    }

    // Finalize (no payment): mark ready, return the shareable /e/{id} link, email it to guests if chosen.
    [HttpPost("{id:guid}/finalize")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> Finalize(Guid id, CancellationToken ct) =>
        Success(await campaigns.FinalizeAsync(id, ct));

    [HttpPut("{id:guid}/roles")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> SetRoles(Guid id, [FromBody] SetRolesRequest req, CancellationToken ct)
    {
        await campaigns.SetRolesAsync(id, req, ct);
        return SuccessMessage("Roles updated.");
    }

    // Inviter uploads an image for a template image slot (multipart). Ownership is enforced in the service.
    /// <summary>
    /// Uploads one or more images for a slot. A gallery slot (<c>data-multiple</c>) sends several at
    /// once and gets a result per file, in the order they were picked; a single slot sends one.
    /// </summary>
    [HttpPost("{id:guid}/images")]
    [HasPermission(Permissions.Campaigns.Write)]
    public async Task<IActionResult> UploadImage(
        Guid id, [FromForm] IFormFileCollection? files, IFormFile? file, [FromForm] string? slot, CancellationToken ct)
    {
        // `file` is the long-standing single-image field; `files` is the multi-image one. Accept both.
        var uploads = (files is { Count: > 0 } ? files.AsEnumerable() : [])
            .Concat(file is not null ? [file] : Array.Empty<IFormFile>())
            .Where(f => f.Length > 0)
            .ToList();

        if (uploads.Count == 0)
            return BadRequest(Application.Common.ApiResponse<object?>.Fail("An image file is required."));

        var results = new List<Application.Dtos.Campaigns.CampaignImageDto>();
        foreach (var upload in uploads)
        {
            await using var stream = upload.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            results.Add(await campaigns.AddImageAsync(
                id, buffer.ToArray(), upload.ContentType, upload.FileName, slot, ct));
        }

        // One file still returns the single object the existing builder expects.
        return Created(results.Count == 1 ? results[0] : (object)results);
    }

    [HttpGet("{id:guid}/summary")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> GetSummary(Guid id, CancellationToken ct) =>
        Success(await campaigns.GetSummaryAsync(id, ct));

    [HttpGet("{id:guid}/pricing")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> GetPricing(Guid id, [FromQuery] int? inviteCount, CancellationToken ct) =>
        Success(await campaigns.GetPricingAsync(id, inviteCount, ct));

    // Public recovery path — emails the links, never displays them. Rate limited to curb enumeration.
    [HttpPost("access/resend-link")]
    [HasPermission(Permissions.Dashboard.Read)]
    [EnableRateLimiting("resend")]
    public async Task<IActionResult> ResendLink([FromBody] ResendLinkRequest req, CancellationToken ct)
    {
        await campaigns.ResendLinkAsync(req, ct);
        return SuccessMessage("If that email has campaigns, we've sent the links.");
    }

    // Magic-link dashboard (§13.3). Public role holds Dashboard.Read; the ?token= is validated
    // (hashed + matched) in the service. Mapped explicitly off the campaigns route prefix.
    [HttpGet("/api/dashboard/{id:guid}")]
    [HasPermission(Permissions.Dashboard.Read)]
    public async Task<IActionResult> GetDashboard(Guid id, [FromQuery] string? token, CancellationToken ct) =>
        Success(await campaigns.GetDashboardAsync(id, token, ct));

    [HttpPost("{id:guid}/cancel")]
    [HasPermission(Permissions.Campaigns.Cancel)]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct) =>
        Success(await campaigns.CancelAsync(id, ct));

    [HttpDelete("{id:guid}")]
    [HasPermission(Permissions.Campaigns.Delete)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct) =>
        Success(await campaigns.DeleteAsync(id, ct));

    /// <summary>Reads the raw campaign possession token from the Authorization header (for the resume link).</summary>
    private string? BearerToken()
    {
        var header = Request.Headers.Authorization.ToString();
        return header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : null;
    }
}
