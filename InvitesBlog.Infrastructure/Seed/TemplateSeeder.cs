using System.Reflection;
using System.Text.Json;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Templates;
using InvitesBlog.TemplateCompiler;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Seed;

/// <summary>
/// Applies migrations and seeds the gallery from the embedded scene files: each scene is compiled
/// and published to storage, then registered as an active <see cref="Template"/> (§16.2 — admin
/// templates use the same pipeline as the designer).
/// </summary>
public sealed class TemplateSeeder(
    AppDbContext db,
    TemplatePackagePublisher publisher,
    ILogger<TemplateSeeder> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await db.Database.MigrateAsync(ct);

        var asm = Assembly.GetExecutingAssembly();
        var resourceNames = asm.GetManifestResourceNames()
            .Where(n => n.Contains(".Seed.Scenes.") && n.EndsWith(".json"))
            .ToList();

        foreach (var name in resourceNames)
        {
            await using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            var json = await reader.ReadToEndAsync(ct);
            var scene = JsonSerializer.Deserialize<Scene>(json, JsonOpts);
            if (scene is null) continue;

            // Always (re)publish the package files so a fresh container's storage is populated even
            // when the template row already exists in a persisted database — the write is idempotent.
            var published = await publisher.PublishAsync(scene, ct);
            foreach (var w in published.Compiled.Warnings)
                logger.LogWarning("Seed {Slug}: {Warning}", scene.Slug, w);

            var existing = await db.Templates
                .FirstOrDefaultAsync(t => t.Slug == scene.Slug && t.Version == scene.Version, ct);
            if (existing is not null)
            {
                logger.LogInformation("Template {Slug}@{Version} package refreshed.", scene.Slug, scene.Version);
                continue;
            }

            db.Templates.Add(new Template
            {
                Id = Guid.NewGuid(),
                Name = scene.Name,
                Slug = scene.Slug,
                Version = scene.Version,
                Category = scene.Category,
                Description = $"A premium {scene.Category.ToLowerInvariant()} invitation template.",
                PreviewImageUrl = $"{published.PackageUrl}index.html",
                PreviewAnimationUrl = null,
                IsPremium = false,
                DesignerInviterId = null,
                DesignerName = "invites.blog",
                SceneJson = json,
                ManifestJson = published.Compiled.ManifestJson,
                PackageUrl = published.PackageUrl,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow
            });
            logger.LogInformation("Seeded template {Slug}@{Version}.", scene.Slug, scene.Version);
        }

        await db.SaveChangesAsync(ct);
    }
}
