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
    IUnitOfWork uow) : BaseApiController
{
    public sealed record UploadResultDto(Guid Id, string Slug, string Version, string PackageUrl,
        IReadOnlyList<string> Variables, IReadOnlyList<string> ContentBlocks);

    /// <summary>
    /// POST /api/admin/templates (multipart) — fields: name, slug, version?, category, description?;
    /// files: index (HTML, required), styles (CSS, optional).
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
        IFormFile? styles,
        CancellationToken ct)
    {
        if (index is null || index.Length == 0)
            return BadRequest(Application.Common.ApiResponse<object?>.Fail("An index.html file is required."));

        version = string.IsNullOrWhiteSpace(version) ? "1.0.0" : version.Trim();
        slug = slug.Trim().ToLowerInvariant();

        var html = await ReadAsync(index, ct);
        var css = styles is { Length: > 0 } ? await ReadAsync(styles, ct) : null;

        var published = await packager.PublishAsync(slug, version, html, css, ct);

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
