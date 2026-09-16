using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Designs;
using InvitesBlog.Application.Services.Designs;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// The visual template designer. Every action is the signed-in account's own; the service re-checks
/// ownership on each one.
/// </summary>
[Route("api/designs")]
[HasPermission(Permissions.Designs.Manage)]
[RequiresFeature(Domain.Entities.Features.TemplateDesigner)]
public sealed class DesignsController(IDesignService designs) : BaseApiController
{
    [HttpGet("catalog")]
    public IActionResult Catalog() => Success(designs.Catalog());

    [HttpGet]
    public async Task<IActionResult> Mine(CancellationToken ct) => Success(await designs.ListMineAsync(ct));

    [HttpPost]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> Create([FromBody] CreateDesignRequest request, CancellationToken ct) =>
        Created(await designs.CreateAsync(request, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Success(await designs.GetAsync(id, ct));

    /// <summary>Autosave. Refused with 409 when <c>baseRevision</c> is stale.</summary>
    [HttpPut("{id:guid}")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> Save(Guid id, [FromBody] SaveDesignRequest request, CancellationToken ct) =>
        Success(await designs.SaveAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await designs.DeleteAsync(id, ct);
        return SuccessMessage("Design deleted.");
    }

    [HttpPost("{id:guid}/duplicate")]
    public async Task<IActionResult> Duplicate(Guid id, CancellationToken ct) =>
        Created(await designs.DuplicateAsync(id, ct));

    /// <summary>
    /// Compiles an unsaved scene with sample data for the editor's preview. Stateless on purpose: the
    /// preview follows every keystroke, and making it wait on a save would make it lag the canvas.
    /// </summary>
    [HttpPost("preview")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    [EnableRateLimiting("design-preview")]
    public IActionResult Preview([FromBody] DesignPreviewRequest request) => Success(designs.Preview(request));

    [HttpGet("{id:guid}/check")]
    public async Task<IActionResult> Check(Guid id, CancellationToken ct) => Success(await designs.CheckAsync(id, ct));

    [HttpPost("assets")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    [EnableRateLimiting("design-assets")]
    public async Task<IActionResult> ImportAsset(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return Success(designs.ImportAsset(buffer.ToArray(), file.FileName, file.ContentType ?? string.Empty));
    }

    /// <summary>What the editor needs to open an existing template: nothing (it was designed) or the document to convert.</summary>
    [HttpGet("import/{templateId:guid}")]
    public async Task<IActionResult> ImportSource(Guid templateId, CancellationToken ct) =>
        Success(await designs.ImportSourceAsync(templateId, ct));

    [HttpPost("{id:guid}/publish")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public async Task<IActionResult> Publish(
        Guid id,
        [FromForm] string visibility,
        [FromForm] string name,
        [FromForm] string category,
        [FromForm] string? description,
        [FromForm] Guid? campaignId,
        [FromForm] int revision,
        [FromForm] string? assignedEmail,
        IFormFile? poster,
        CancellationToken ct)
    {
        byte[]? posterBytes = null;
        if (poster is { Length: > 0 })
        {
            await using var stream = poster.OpenReadStream();
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, ct);
            posterBytes = buffer.ToArray();
        }
        return Success(await designs.PublishAsync(id, new PublishDesignRequest(
            visibility, name, category, description, campaignId, revision, posterBytes, poster?.ContentType, assignedEmail), ct));
    }

    [HttpPut("{id:guid}/visibility")]
    public async Task<IActionResult> SetVisibility(Guid id, [FromBody] SetDesignVisibilityRequest request, CancellationToken ct) =>
        Success(await designs.SetVisibilityAsync(id, request, ct));

    [HttpGet("{id:guid}/events")]
    public async Task<IActionResult> Events(Guid id, CancellationToken ct) => Success(await designs.EventsAsync(id, ct));

    [HttpPost("{id:guid}/events/{campaignId:guid}/upgrade")]
    public async Task<IActionResult> UpgradeEvent(Guid id, Guid campaignId, CancellationToken ct) =>
        Success(await designs.UpgradeEventAsync(id, campaignId, ct));
}

/// <summary>Reporting a gallery template, and the admin queue that handles reports.</summary>
[Route("api")]
public sealed class TemplateReportsController(ITemplateReportService reports) : BaseApiController
{
    /// <summary>Open to anyone browsing the gallery, signed in or not; rate limited per address.</summary>
    [HttpPost("templates/{templateId:guid}/reports")]
    [HasPermission(Permissions.Templates.Read)]
    [EnableRateLimiting("template-report")]
    public async Task<IActionResult> Report(Guid templateId, [FromBody] ReportTemplateRequest request, CancellationToken ct)
    {
        await reports.ReportAsync(templateId, request, ct);
        return SuccessMessage("Thanks — we'll take a look.");
    }

    [HttpGet("admin/template-reports")]
    [HasPermission(Permissions.Designs.Moderate)]
    public async Task<IActionResult> List([FromQuery] string? status, CancellationToken ct) =>
        Success(await reports.ListAsync(status, ct));

    [HttpPost("admin/template-reports/{reportId:guid}/resolve")]
    [HasPermission(Permissions.Designs.Moderate)]
    public async Task<IActionResult> Resolve(Guid reportId, [FromBody] ResolveReportRequest request, CancellationToken ct) =>
        Success(await reports.ResolveAsync(reportId, request, ct));

    [HttpPost("admin/templates/{templateId:guid}/relist")]
    [HasPermission(Permissions.Designs.Moderate)]
    public async Task<IActionResult> Relist(Guid templateId, CancellationToken ct)
    {
        await reports.RelistAsync(templateId, ct);
        return SuccessMessage("The admin hold is lifted.");
    }

    [HttpPost("admin/users/{userId:guid}/public-publishing")]
    [HasPermission(Permissions.Designs.Moderate)]
    public async Task<IActionResult> RestorePublishing(Guid userId, CancellationToken ct)
    {
        await reports.RestorePublishingAsync(userId, ct);
        return SuccessMessage("Gallery publishing restored.");
    }
}
