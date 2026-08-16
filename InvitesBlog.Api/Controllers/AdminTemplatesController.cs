using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Filters.Templates;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Templates;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

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
    Application.Abstractions.IStorageService storage,
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

    /// <summary>
    /// GET /api/admin/templates — the management list (newest first). Paged, searchable (name/slug),
    /// filterable by category, and split by <c>status</c> tab: <c>active</c> (default), <c>inactive</c>, <c>all</c>.
    /// </summary>
    [HttpGet]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> List([FromQuery] AdminTemplateFilter filter, CancellationToken ct)
    {
        var query = templates.Query();

        query = filter.Status?.Trim().ToLowerInvariant() switch
        {
            "inactive" => query.Where(t => !t.IsActive),
            "all" => query,
            _ => query.Where(t => t.IsActive), // default: active only
        };
        if (!string.IsNullOrWhiteSpace(filter.Category))
            query = query.Where(t => t.Category == filter.Category);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            query = query.Where(t => t.Name.ToLower().Contains(term) || t.Slug.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var page = await query
            .OrderByDescending(t => t.CreatedAt)
            .Skip(filter.Skip).Take(filter.PageSize)
            .ToListAsync(ct);

        var items = new List<AdminTemplateDto>(page.Count);
        foreach (var t in page)
        {
            var count = await campaigns.CountAsync(c => c.TemplateId == t.Id, ct);
            items.Add(new AdminTemplateDto(t.Id, t.Name, t.Slug, t.Category, t.Version, t.PackageUrl,
                t.Visibility, t.IsActive, t.AssignedEmail, count));
        }
        return Paged(PagedResult<AdminTemplateDto>.Create(items, total, filter));
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
    /// files: index (a single self-contained HTML file with inline CSS, required) and preview (a static
    /// card image, optional — without one the gallery falls back to rendering the live page).
    /// </summary>
    [HttpPost]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> Upload(
        [FromForm] string name,
        [FromForm] string slug,
        [FromForm] string category,
        IFormFile index,
        IFormFile? preview,
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

        // A real static image beats pointing the card at the live page — see the gallery's fallback.
        var previewUrl = await StorePreviewAsync(slug, version, preview, ct)
                         ?? $"{published.PackageUrl}index.html";

        // Match on SLUG alone, not slug+version: a gallery card is one template, and uploading a new
        // version SUPERSEDES it rather than adding a second card for the same design. The previous
        // version's package stays on disk, and campaigns pinned to it keep serving it.
        var existing = await templates.FirstOrDefaultAsync(t => t.Slug == slug, ct);
        Template entity;
        if (existing is not null)
        {
            entity = (await templates.GetByIdAsync(existing.Id, ct))!;
            entity.Name = name;
            entity.Category = category;
            entity.Description = description ?? entity.Description;
            entity.Version = version;
            entity.ManifestJson = published.ManifestJson;
            entity.PackageUrl = published.PackageUrl;
            // Keep an already-uploaded static preview when this upload didn't bring a new one.
            if (preview is not null || entity.PreviewImageUrl.EndsWith("index.html", StringComparison.OrdinalIgnoreCase))
                entity.PreviewImageUrl = previewUrl;
            entity.IsActive = true;
            entity.Visibility = isDedicated ? TemplateVisibility.Dedicated : TemplateVisibility.Public;
            entity.AssignedEmail = normalizedEmail;
            entity.UpdatedAt = DateTimeOffset.UtcNow;
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
                PreviewImageUrl = previewUrl,
                IsPremium = false,
                DesignerName = "invites.blog",
                SceneJson = "{}",
                ManifestJson = published.ManifestJson,
                PackageUrl = published.PackageUrl,
                IsActive = true,
                Visibility = isDedicated ? TemplateVisibility.Dedicated : TemplateVisibility.Public,
                AssignedEmail = normalizedEmail,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            await templates.AddAsync(entity, ct);
        }
        await uow.SaveChangesAsync(ct);

        return Created(new UploadResultDto(entity.Id, slug, version, published.PackageUrl,
            published.Manifest.Variables, published.Manifest.ContentBlocks));
    }

    /// <summary>Stores an uploaded card image beside the template package; null when none was sent.</summary>
    private async Task<string?> StorePreviewAsync(string slug, string version, IFormFile? preview, CancellationToken ct)
    {
        if (preview is null || preview.Length == 0) return null;
        if (!preview.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new Application.Exceptions.BusinessRuleException(
                "The preview must be an image (PNG or JPEG).", "template_preview_not_an_image");

        await using var stream = preview.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);

        var extension = Path.GetExtension(preview.FileName);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
        return await storage.PutAsync(
            $"templates/{slug}@{version}/preview{extension.ToLowerInvariant()}",
            buffer.ToArray(), preview.ContentType, ct);
    }

    private static async Task<string> ReadAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}
