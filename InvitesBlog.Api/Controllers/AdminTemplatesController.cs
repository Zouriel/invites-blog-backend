using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Templates;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// Admin-only raw HTML/CSS template management (§16.2). Only a principal holding
/// <c>templates.manage</c> (the Admin role) may upload; uploaded templates then appear in the public
/// gallery for inviters to choose. Re-uploading the same slug+version updates it in place.
/// </summary>
[Route("api/admin/templates")]
public sealed class AdminTemplatesController(
    RawTemplatePackager packager,
    ITemplateRepository templates,
    ICampaignRepository campaigns,
    IUnitOfWork uow) : BaseApiController
{
    public sealed record UploadResultDto(Guid Id, string Slug, string Version, string PackageUrl,
        IReadOnlyList<string> Variables, IReadOnlyList<string> ContentBlocks);

    /// <summary>An admin management row — every template (incl. inactive/dedicated) plus how many
    /// campaigns already use it, so the admin knows whether a delete will hard-delete or deactivate.</summary>
    public sealed record AdminTemplateDto(
        Guid Id, string Name, string Slug, string Category, string Version, string PackageUrl,
        string Visibility, bool IsActive, string? AssignedEmail, int CampaignCount);

    public sealed record DeleteResultDto(bool Deleted, bool Deactivated, int CampaignCount);

    /// <summary>GET /api/admin/templates — every template for the management list (newest first).</summary>
    [HttpGet]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var all = await templates.ListAsync(ct: ct);
        var items = new List<AdminTemplateDto>(all.Count);
        foreach (var t in all.OrderByDescending(x => x.CreatedAt))
        {
            var count = await campaigns.CountAsync(c => c.TemplateId == t.Id, ct);
            items.Add(new AdminTemplateDto(t.Id, t.Name, t.Slug, t.Category, t.Version, t.PackageUrl,
                t.Visibility, t.IsActive, t.AssignedEmail, count));
        }
        return Success(items);
    }

    /// <summary>
    /// DELETE /api/admin/templates/{id} — removes a template. If any campaign already uses it, the row
    /// is DEACTIVATED (hidden from the gallery) instead of hard-deleted, so invites already created from
    /// it keep rendering their stored package. Unused templates are hard-deleted.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var entity = await templates.GetByIdAsync(id, ct);
        if (entity is null)
            return NotFound(Application.Common.ApiResponse<object?>.Fail("Template not found."));

        var campaignCount = await campaigns.CountAsync(c => c.TemplateId == id, ct);
        if (campaignCount > 0)
        {
            entity.IsActive = false;
            templates.Update(entity);
            await uow.SaveChangesAsync(ct);
            return Success(new DeleteResultDto(false, true, campaignCount),
                $"“{entity.Name}” is used by {campaignCount} campaign(s), so it was deactivated (hidden from the gallery) rather than deleted.");
        }

        templates.Remove(entity);
        await uow.SaveChangesAsync(ct);
        return Success(new DeleteResultDto(true, false, 0), $"“{entity.Name}” was deleted.");
    }

    /// <summary>
    /// POST /api/admin/templates (multipart) — fields: name, slug, version?, category, description?;
    /// file: index (a single self-contained HTML file with inline CSS + JS, required).
    /// </summary>
    [HttpPost]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> Upload(
        [FromForm] string name,
        [FromForm] string slug,
        [FromForm] string category,
        IFormFile index,
        [FromForm] string? version,
        [FromForm] string? description,
        [FromForm] string? visibility,
        [FromForm] string? assignedEmail,
        CancellationToken ct)
    {
        if (index is null || index.Length == 0)
            return BadRequest(Application.Common.ApiResponse<object?>.Fail("An index.html file is required."));

        // Public (gallery) vs Dedicated (reserved for one requester's email).
        var isDedicated = string.Equals(visibility, TemplateVisibility.Dedicated, StringComparison.OrdinalIgnoreCase);
        var normalizedEmail = isDedicated ? (assignedEmail ?? "").Trim().ToLowerInvariant() : null;
        if (isDedicated && string.IsNullOrWhiteSpace(normalizedEmail))
            return BadRequest(Application.Common.ApiResponse<object?>.Fail("A dedicated template requires an assigned email."));

        version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version.Trim();
        slug = slug.Trim().ToLowerInvariant();

        // A template is a single self-contained HTML file (CSS + JS inlined); enforced by the packager.
        var html = await ReadAsync(index, ct);
        var published = await packager.PublishAsync(slug, version, html, ct: ct);

        var existing = await templates.FirstOrDefaultAsync(t => t.Slug == slug && t.Version == version, ct);
        Template entity;
        if (existing is not null)
        {
            entity = (await templates.GetByIdAsync(existing.Id, ct))!;
            entity.Name = name;
            entity.Category = category;
            entity.Description = description ?? entity.Description;
            entity.ManifestJson = published.ManifestJson;
            entity.PackageUrl = published.PackageUrl;
            entity.PreviewImageUrl = $"{published.PackageUrl}index.html";
            entity.IsActive = true;
            entity.Visibility = isDedicated ? TemplateVisibility.Dedicated : TemplateVisibility.Public;
            entity.AssignedEmail = normalizedEmail;
            templates.Update(entity);
        }
        else
        {
            entity = new Template
            {
                Id = Guid.NewGuid(),
                Name = name,
                Slug = slug,
                Version = version,
                Category = category,
                Description = description ?? $"A {category.ToLowerInvariant()} invitation template.",
                PreviewImageUrl = $"{published.PackageUrl}index.html",
                IsPremium = false,
                DesignerName = "invites.blog",
                SceneJson = "{}",
                ManifestJson = published.ManifestJson,
                PackageUrl = published.PackageUrl,
                IsActive = true,
                Visibility = isDedicated ? TemplateVisibility.Dedicated : TemplateVisibility.Public,
                AssignedEmail = normalizedEmail,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await templates.AddAsync(entity, ct);
        }
        await uow.SaveChangesAsync(ct);

        return Created(new UploadResultDto(entity.Id, slug, version, published.PackageUrl,
            published.Manifest.Variables, published.Manifest.ContentBlocks));
    }

    private static async Task<string> ReadAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}
