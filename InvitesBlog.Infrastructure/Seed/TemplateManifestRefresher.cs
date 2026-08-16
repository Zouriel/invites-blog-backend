using System.Text;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Templates;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Seed;

/// <summary>
/// Re-derives every stored single-file template's manifest from its served <c>index.html</c> on startup,
/// so a change to the extraction rules (e.g. case-insensitive field/slot dedupe) heals templates uploaded
/// under the old rules — without re-uploading. Idempotent: only writes when the manifest actually changed.
/// <para>
/// Only RAW/admin single-file templates (empty <c>SceneJson</c>) are refreshed — their whole manifest is
/// derivable from the one HTML file. COMPILED templates (real <c>SceneJson</c>) are skipped: their
/// manifest carries roles / editable areas / gender variants the tag scan can't reproduce, so re-deriving
/// would wipe them.
/// </para>
/// </summary>
public sealed class TemplateManifestRefresher(
    AppDbContext db,
    RawTemplatePackager packager,
    IStorageService storage,
    ILogger<TemplateManifestRefresher> logger)
{
    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = false };

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var templates = await db.Templates.ToListAsync(ct);
        var changed = 0;

        foreach (var t in templates)
        {
            // Skip compiled templates — their manifest is authored from the scene, not the HTML.
            if (!string.IsNullOrWhiteSpace(t.SceneJson) && t.SceneJson.Trim() is not "{}") continue;

            var bytes = await storage.GetAsync($"templates/{t.Slug}@{t.Version}/index.html", ct);
            if (bytes is null)
            {
                logger.LogWarning("Template {Slug}@{Version} has no stored index.html — manifest not refreshed.", t.Slug, t.Version);
                continue;
            }

            string manifestJson;
            try
            {
                var manifest = packager.BuildManifest(t.Slug, t.Version, Encoding.UTF8.GetString(bytes));
                manifestJson = JsonSerializer.Serialize(manifest, JsonOut);
            }
            catch (AppException ex)
            {
                // A stored template that violates a NEWER authoring rule must not take startup down with
                // it — leave its existing manifest alone and surface it for the admin to fix.
                logger.LogError(ex, "Template {Slug}@{Version} failed manifest re-derivation — keeping the stored manifest.",
                    t.Slug, t.Version);
                continue;
            }

            if (string.Equals(manifestJson, t.ManifestJson, StringComparison.Ordinal)) continue;

            t.ManifestJson = manifestJson;
            await storage.PutAsync($"templates/{t.Slug}@{t.Version}/manifest.json",
                Encoding.UTF8.GetBytes(manifestJson), "application/json", ct);
            changed++;
            logger.LogInformation("Refreshed manifest for {Slug}@{Version} (deduped fields/image slots).", t.Slug, t.Version);
        }

        if (changed > 0) await db.SaveChangesAsync(ct);
    }
}
