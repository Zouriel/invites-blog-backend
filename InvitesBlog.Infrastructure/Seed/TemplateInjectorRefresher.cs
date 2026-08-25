using System.Text;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Templates;
using InvitesBlog.TemplateCompiler;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Seed;

/// <summary>
/// Re-publishes community templates whose stored package still carries an OLD copy of the trusted
/// injector.
/// <para>
/// The injector is platform code, but it's inlined into each package at publish time — so shipping a
/// fix to it does nothing for templates already on disk. Committed templates self-heal because
/// <see cref="RawTemplateSeeder"/> republishes them every startup; approved community submissions had
/// no equivalent, which would have left them silently missing whatever the fix added.
/// </para>
/// <para>
/// The original source is re-published from <c>CustomTemplate.Html</c> — the verbatim submission we
/// keep for audit — so nothing is re-derived from already-injected HTML. Only the template's CURRENT
/// version is refreshed; older versions a campaign is still pinned to stay byte-for-byte as sent.
/// </para>
/// </summary>
public sealed class TemplateInjectorRefresher(
    AppDbContext db,
    RawTemplatePackager packager,
    IStorageService storage,
    ILogger<TemplateInjectorRefresher> logger)
{
    /// <summary>
    /// A distinctive fragment of the CURRENT injector. A stored package that lacks it predates the
    /// present build and is republished. Update this whenever the injector gains behaviour that has
    /// to reach already-published templates.
    /// </summary>
    private const string CurrentInjectorMarker = "applyTheme(data.themeVars)";

    public async Task RefreshAsync(CancellationToken ct = default)
    {
        // Only templates that came through the review pipeline — those are the ones with saved source
        // and no other refresh path.
        var published = await db.CustomTemplates
            .Where(c => c.Status == CustomTemplateStatus.Published && c.PublishedTemplateId != null)
            .ToListAsync(ct);
        if (published.Count == 0) return;

        var templateIds = published.Select(c => c.PublishedTemplateId!.Value).ToList();
        var templates = await db.Templates
            .Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        var refreshed = 0;
        foreach (var submission in published)
        {
            if (!templates.TryGetValue(submission.PublishedTemplateId!.Value, out var template)) continue;
            if (string.IsNullOrWhiteSpace(submission.Html)) continue;

            var basePath = $"templates/{template.Slug}@{template.Version}";
            var stored = await storage.GetAsync($"{basePath}/index.html", ct);
            if (stored is not null &&
                Encoding.UTF8.GetString(stored).Contains(CurrentInjectorMarker, StringComparison.Ordinal))
                continue; // already on the current injector

            try
            {
                await packager.PublishToAsync(basePath, template.Slug, template.Version, submission.Html, ct: ct);
                refreshed++;
                logger.LogInformation(
                    "Re-published {Slug}@{Version} with the current injector.", template.Slug, template.Version);
            }
            catch (AppException ex)
            {
                // A template that no longer passes today's scan must not take startup down — it stays
                // on its old package, still serving, and the failure is surfaced for an admin.
                logger.LogError(ex,
                    "Template {Slug}@{Version} could not be re-published — it keeps its existing package.",
                    template.Slug, template.Version);
            }
        }

        if (refreshed > 0)
            logger.LogInformation("Injector refresh complete — {Count} template package(s) updated.", refreshed);
    }
}
