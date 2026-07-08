using System.Reflection;
using System.Text.Json;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Seed;

/// <summary>
/// Seeds admin-authored raw HTML/CSS templates that ship in the repo under
/// <c>RawTemplates/{slug}/</c> (index.html + styles.css + meta.json, embedded). Each is packaged and
/// registered as an active gallery template. Committing a new folder is how the owner "adds" a template.
/// </summary>
public sealed class RawTemplateSeeder(
    AppDbContext db,
    RawTemplatePackager packager,
    ILogger<RawTemplateSeeder> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record RawMeta(string Name, string Slug, string Version, string Category, string? Description);

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var asm = Assembly.GetExecutingAssembly();
        var metas = asm.GetManifestResourceNames()
            .Where(n => n.Contains(".RawTemplates.") && n.EndsWith(".meta.json"))
            .ToList();

        foreach (var metaResource in metas)
        {
            var prefix = metaResource[..^".meta.json".Length]; // ...RawTemplates.{slug}
            var meta = JsonSerializer.Deserialize<RawMeta>(await ReadAsync(asm, metaResource, ct)!, JsonOpts);
            if (meta is null) continue;

            var html = await ReadAsync(asm, prefix + ".index.html", ct);
            if (html is null)
            {
                logger.LogWarning("Raw template {Slug} has no index.html — skipped.", meta.Slug);
                continue;
            }

            // Always (re)publish so a fresh container's storage is populated.
            var published = await packager.PublishAsync(meta.Slug, meta.Version, html, ct: ct);

            // If it already exists, refresh the package AND its manifest — editing a raw template's HTML
            // (new fields, image slots, blocks) must flow into the stored manifest, not just storage.
            var existing = await db.Templates.FirstOrDefaultAsync(t => t.Slug == meta.Slug && t.Version == meta.Version, ct);
            if (existing is not null)
            {
                existing.Name = meta.Name;
                existing.Category = meta.Category;
                existing.Description = meta.Description ?? existing.Description;
                existing.ManifestJson = published.ManifestJson;
                existing.PackageUrl = published.PackageUrl;
                existing.PreviewImageUrl = $"{published.PackageUrl}index.html";
                existing.IsActive = true;
                logger.LogInformation("Raw template {Slug}@{Version} refreshed (package + manifest).", meta.Slug, meta.Version);
                continue;
            }

            db.Templates.Add(new Template
            {
                Id = Guid.NewGuid(),
                Name = meta.Name,
                Slug = meta.Slug,
                Version = meta.Version,
                Category = meta.Category,
                Description = meta.Description ?? $"A {meta.Category.ToLowerInvariant()} invitation template.",
                PreviewImageUrl = $"{published.PackageUrl}index.html",
                IsPremium = false,
                DesignerName = "invites.blog",
                SceneJson = "{}",                       // raw templates have no SceneJson source
                ManifestJson = published.ManifestJson,
                PackageUrl = published.PackageUrl,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
            logger.LogInformation("Seeded raw template {Slug}@{Version}.", meta.Slug, meta.Version);
        }

        await db.SaveChangesAsync(ct);
    }

    private static async Task<string?> ReadAsync(Assembly asm, string resource, CancellationToken ct)
    {
        await using var stream = asm.GetManifestResourceStream(resource);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}
