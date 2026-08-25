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
/// <para>
/// A folder may ship private: <c>"visibility": "Dedicated"</c> plus an <c>"assignedEmail"</c> reserves
/// the template for that one address (it never appears in the public gallery, only under
/// <c>/api/me/dedicated-templates</c> once that email verifies by OTP). Omit both for a normal
/// public gallery template.
/// </para>
/// </summary>
public sealed class RawTemplateSeeder(
    AppDbContext db,
    RawTemplatePackager packager,
    ILogger<RawTemplateSeeder> logger)
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed record RawMeta(
        string Name, string Slug, string Version, string Category, string? Description,
        string? Visibility, string? AssignedEmail);

    public async Task SeedAsync(CancellationToken ct = default)
    {
        var asm = Assembly.GetExecutingAssembly();
        var metas = asm.GetManifestResourceNames()
            .Where(n => n.Contains(".RawTemplates.") && n.EndsWith(".meta.json"))
            .ToList();

        foreach (var metaResource in metas)
        {
            var prefix = metaResource[..^".meta.json".Length]; // ...RawTemplates.{slug}

            // Skip rather than throw: this runs during startup, so an unreadable resource would turn
            // a single bad template into a boot loop for the whole API.
            var metaJson = await ReadAsync(asm, metaResource, ct);
            if (metaJson is null)
            {
                logger.LogWarning("Raw template metadata {Resource} could not be read — skipped.", metaResource);
                continue;
            }

            var meta = JsonSerializer.Deserialize<RawMeta>(metaJson, JsonOpts);
            if (meta is null) continue;

            // Public (listed in the gallery) vs Dedicated (reserved for one address). A Dedicated
            // folder with no address is a broken privacy declaration, so fail CLOSED — skipping keeps
            // the template out of the gallery, whereas defaulting to Public would publish something
            // the author meant to keep private.
            var isDedicated = string.Equals(meta.Visibility, TemplateVisibility.Dedicated,
                StringComparison.OrdinalIgnoreCase);
            var assignedEmail = isDedicated ? (meta.AssignedEmail ?? "").Trim().ToLowerInvariant() : null;
            if (isDedicated && string.IsNullOrWhiteSpace(assignedEmail))
            {
                logger.LogWarning(
                    "Raw template {Slug} declares Dedicated visibility with no assignedEmail — skipped.",
                    meta.Slug);
                continue;
            }

            var html = await ReadAsync(asm, prefix + ".index.html", ct);
            if (html is null)
            {
                logger.LogWarning("Raw template {Slug} has no index.html — skipped.", meta.Slug);
                continue;
            }

            // A template may ship its own card image. Optional: without one the gallery falls back to
            // rendering the template itself, which still works and just costs more.
            var poster = await ReadBytesAsync(asm, prefix + ".poster.webp", ct);

            // Always (re)publish so a fresh container's storage is populated.
            // These templates ship in this repository and are reviewed like any other source file,
            // so they may carry their own script. Designer submissions still may not.
            var published = await packager.PublishAsync(
                meta.Slug, meta.Version, html, allowScripts: true, ct: ct, poster: poster);

            // Match on SLUG alone. Matching slug+version would treat a version bump as a brand-new
            // template and leave TWO cards for the same design in the gallery; a bump SUPERSEDES.
            // The previous version's package stays in storage, and campaigns pinned to it keep
            // serving it via the package URL they froze at creation.
            var existing = await db.Templates.FirstOrDefaultAsync(t => t.Slug == meta.Slug, ct);
            if (existing is not null)
            {
                var superseded = existing.Version != meta.Version;
                existing.Name = meta.Name;
                existing.Category = meta.Category;
                existing.Description = meta.Description ?? existing.Description;
                existing.Version = meta.Version;
                existing.ManifestJson = published.ManifestJson;
                existing.PackageUrl = published.PackageUrl;
                // A first-party template SHIPS its card image, so the shipped one wins — that is how a
                // corrected poster reaches the gallery, and its filename changes with its content.
                // Without a shipped poster, only the live-page stand-in is replaced: a stand-in is any
                // URL still pointing at index.html, which is a page, not an image.
                if (published.PosterUrl is { Length: > 0 })
                    existing.PreviewImageUrl = published.PosterUrl;
                else if (existing.PreviewImageUrl.EndsWith("index.html", StringComparison.OrdinalIgnoreCase)
                         || string.IsNullOrWhiteSpace(existing.PreviewImageUrl))
                    existing.PreviewImageUrl = $"{published.PackageUrl}index.html";
                // Re-apply the declared visibility only while the row is STILL dedicated. Dedicated to
                // Public is a one-way door owned by the consent flow (TemplateReleaseService) and by
                // admin, so a restart must never quietly reverse a release — or re-privatize a template
                // the gallery is already showing.
                if (existing.Visibility == TemplateVisibility.Dedicated)
                {
                    existing.Visibility = isDedicated ? TemplateVisibility.Dedicated : TemplateVisibility.Public;
                    existing.AssignedEmail = isDedicated ? assignedEmail : null;
                }
                existing.IsActive = true;
                existing.UpdatedAt = DateTimeOffset.UtcNow;
                logger.LogInformation(
                    superseded
                        ? "Raw template {Slug} superseded to @{Version} (package + manifest)."
                        : "Raw template {Slug}@{Version} refreshed (package + manifest).",
                    meta.Slug, meta.Version);
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
                PreviewImageUrl = published.PosterUrl ?? $"{published.PackageUrl}index.html",
                IsPremium = false,
                DesignerName = "invites.blog",
                SceneJson = "{}",                       // raw templates have no SceneJson source
                ManifestJson = published.ManifestJson,
                PackageUrl = published.PackageUrl,
                Visibility = isDedicated ? TemplateVisibility.Dedicated : TemplateVisibility.Public,
                AssignedEmail = assignedEmail,
                IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            logger.LogInformation(
                isDedicated
                    ? "Seeded raw template {Slug}@{Version}, reserved for {Email}."
                    : "Seeded raw template {Slug}@{Version}.",
                meta.Slug, meta.Version, assignedEmail);
        }

        await db.SaveChangesAsync(ct);
    }

    /// <summary>Reads an embedded binary resource (a poster), or null when the template ships none.</summary>
    private static async Task<byte[]?> ReadBytesAsync(Assembly asm, string resource, CancellationToken ct)
    {
        await using var stream = asm.GetManifestResourceStream(resource);
        if (stream is null) return null;
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms, ct);
        return ms.ToArray();
    }

    private static async Task<string?> ReadAsync(Assembly asm, string resource, CancellationToken ct)
    {
        await using var stream = asm.GetManifestResourceStream(resource);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(ct);
    }
}
