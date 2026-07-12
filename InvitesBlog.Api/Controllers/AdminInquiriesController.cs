using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Inquiries;
using InvitesBlog.Application.Filters.Inquiries;
using InvitesBlog.Application.Services.Inquiries;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Infrastructure.Templates;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// Admin triage of custom-invitation inquiries: the queue (unattended first), a detail view, saving the
/// consultation fields, and issuing the dedicated template (which emails the customer their "ready" link).
/// </summary>
[Route("api/admin/inquiries")]
public sealed class AdminInquiriesController(
    IInquiryService inquiries,
    RawTemplatePackager packager) : BaseApiController
{
    [HttpGet]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> List([FromQuery] InquiryFilter filter, CancellationToken ct) =>
        Paged(await inquiries.ListAsync(filter, ct));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Success(await inquiries.GetAsync(id, ct));

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateInquiryRequest req, CancellationToken ct)
    {
        await inquiries.UpdateAsync(id, req, ct);
        return SuccessMessage("Inquiry updated.");
    }

    /// <summary>
    /// POST /api/admin/inquiries/{id}/issue (multipart) — fields: name, slug, category, version?,
    /// description?; file: index (the self-contained HTML). Packages it, issues it as a dedicated template
    /// reserved for the customer's email, flips the inquiry's "issued" flag, and emails the customer.
    /// </summary>
    [HttpPost("{id:guid}/issue")]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> Issue(
        Guid id,
        [FromForm] string name,
        [FromForm] string slug,
        [FromForm] string category,
        IFormFile index,
        [FromForm] string? version,
        [FromForm] string? description,
        CancellationToken ct)
    {
        if (index is null || index.Length == 0)
            return BadRequest(Application.Common.ApiResponse<object?>.Fail("An index.html file is required."));

        version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version.Trim();
        slug = slug.Trim().ToLowerInvariant();

        var html = await ReadAsync(index, ct);
        var published = await packager.PublishAsync(slug, version, html, ct: ct);

        var res = await inquiries.IssueTemplateAsync(id,
            new IssueTemplateData(name, slug, version, category, description, published.ManifestJson, published.PackageUrl), ct);

        return Success(res);
    }

    private static async Task<string> ReadAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}
