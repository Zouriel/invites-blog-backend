using System.Net;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Designs;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Services.Designs;

/// <summary>
/// Bring your own design: the customer supplies the artwork, we supply the evening.
///
/// <para><b>What this deliberately does not do.</b> An imported design is never made dynamic. We do
/// not find its text, we do not guess which words are a name, and nothing inside the artwork changes
/// from one guest to the next — every guest sees the identical picture the customer uploaded. What is
/// still personal is everything around it: the email or Viber message greets each guest by name, the
/// invitation is theirs alone behind their own token, and the RSVP and the media bucket are wrapped
/// around it exactly as they are around a template.</para>
///
/// <para><b>Where the bytes go, and why it matters.</b> The document is stored under a prefix that is
/// NOT served to browsers — <see cref="DocumentPrefix"/> — because the renderer fetches it
/// server-side by storage key and no browser ever needs to request it. That is what makes it safe to
/// accept unreviewed markup from any customer: `/assets/*` is proxied on both app origins with no
/// CSP, and the session token lives in localStorage there, so an uploaded document reachable at an
/// app-origin URL would be an account-takeover vector. Reachable from no browser origin at all is
/// stronger than reachable from an isolated one. Its assets are ordinary inert files and live on the
/// normal campaign path, so they load same-origin under the render policy with nothing widened.</para>
/// </summary>
public interface IImportedDesignService
{
    /// <summary>
    /// Takes an upload and makes it this campaign's design. Replaces whatever was there before, so
    /// re-uploading a corrected export is the normal way to fix a mistake.
    /// </summary>
    Task<ImportedDesignResult> ImportAsync(
        Guid campaignId, byte[] content, string fileName, CancellationToken ct = default);

    /// <summary>
    /// Starts a campaign from a design the customer brought, rather than from the gallery.
    ///
    /// <para>The ordinary create path needs a template to pin, and someone bringing their own has
    /// none — so an empty imported row is made first, the campaign is created against it through the
    /// normal service (tokens, ownership, slug, status: all of it unchanged), and the upload then
    /// fills both in. Nothing about campaign creation is duplicated here.</para>
    /// </summary>
    Task<(CreateCampaignResponse Campaign, ImportedDesignResult Design)> CreateAsync(
        string title, byte[] content, string fileName, CancellationToken ct = default);
}

/// <param name="PreviewUrl">What to show the customer back, so they can see we got the right file.</param>
/// <param name="Kind">"image" or "bundle" — what they actually gave us.</param>
public sealed record ImportedDesignResult(Guid TemplateId, string PackageUrl, string? PreviewUrl, string Kind);

/// <inheritdoc cref="IImportedDesignService"/>
public sealed class ImportedDesignService(
    ICampaignRepository campaigns,
    ICampaignService campaignService,
    IRepository<Template> templates,
    ICampaignOwnershipService ownership,
    IStorageService storage,
    IConfiguration config,
    IUnitOfWork uow) : IImportedDesignService
{
    /// <summary>
    /// Where an imported document lives. Deliberately outside anything the proxy serves — see the
    /// class remarks. If this prefix ever becomes browser-reachable, unreviewed customer HTML starts
    /// executing on an origin that holds a session.
    /// </summary>
    public const string DocumentPrefix = "imported";

    public async Task<(CreateCampaignResponse Campaign, ImportedDesignResult Design)> CreateAsync(
        string title, byte[] content, string fileName, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(title))
            throw new BusinessRuleException("Give your event a name.", "title_required");

        // A placeholder row so the normal create path has something to pin. Its package is empty
        // until the import below writes one, which is the same order the gallery path uses — a
        // template exists before a campaign points at it.
        var placeholder = new Template
        {
            Id = Guid.NewGuid(),
            Name = title.Trim(),
            Slug = $"imported-pending-{Guid.NewGuid():N}",
            Description = ImportedDescription,
            Category = "Imported",
            Version = "1.0.0",
            PackageUrl = string.Empty,
            PreviewImageUrl = string.Empty,
            ManifestJson = Manifest,
            SceneJson = "{}",
            Visibility = TemplateVisibility.Imported,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await templates.AddAsync(placeholder, ct);
        await uow.SaveChangesAsync(ct);

        var created = await campaignService.CreateAsync(
            new CreateCampaignRequest(placeholder.Id, title.Trim()), ct);

        var design = await ImportAsync(created.CampaignId, content, fileName, ct);
        return (created, design);
    }

    public async Task<ImportedDesignResult> ImportAsync(
        Guid campaignId, byte[] content, string fileName, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");

        if (!await ownership.OwnsAsync(campaignId, ct))
            throw new ForbiddenException("That event isn't yours.");

        if (content.Length == 0)
            throw new BusinessRuleException("That file is empty.", "design_empty");

        // One folder per campaign, replaced wholesale on re-upload. Keys carry the campaign so a
        // design can never be served for an event it does not belong to.
        var stem = $"campaigns/{campaign.Id:N}/design";

        string documentHtml;
        string? previewUrl;
        string kind;

        if (ImportedDesignPackage.IsZip(fileName))
        {
            var unpacked = ImportedDesignPackage.Unpack(content);

            // Assets first: the document's references cannot be rewritten until the URLs exist.
            var urls = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var asset in unpacked.Assets)
            {
                urls[asset.Path] = await storage.PutAsync(
                    $"{stem}/{asset.Path}", asset.Content, ContentTypeFor(asset.Path), ct);
            }

            documentHtml = ImportedDesignPackage.Rewrite(
                System.Text.Encoding.UTF8.GetString(unpacked.Document.Content), urls);

            previewUrl = urls.Values.FirstOrDefault();
            kind = "bundle";
        }
        else if (ImportedDesignPackage.IsStandaloneMedia(fileName))
        {
            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            previewUrl = await storage.PutAsync(
                $"{stem}/design{ext}", content, ContentTypeFor(fileName), ct);

            documentHtml = Wrap(previewUrl, campaign.Title, IsVideo(ext));
            kind = "image";
        }
        else
        {
            throw new BusinessRuleException(
                "Upload an image, a video, or a zip of your design.", "design_unsupported");
        }

        // The document goes somewhere the proxy does not serve. Everything above this line is
        // browser-facing and inert; this one file is markup we did not write.
        var packageUrl = await storage.PutAsync(
            $"{DocumentPrefix}/{campaign.Id:N}/index.html",
            System.Text.Encoding.UTF8.GetBytes(documentHtml),
            "text/html; charset=utf-8", ct);

        // The package URL a campaign pins is a FOLDER — the renderer appends index.html to it.
        var packageFolder = packageUrl[..(packageUrl.LastIndexOf('/') + 1)];

        var template = await UpsertTemplateAsync(campaign, packageFolder, previewUrl, ct);

        campaign.TemplateId = template.Id;
        campaign.TemplateVersion = template.Version;
        campaign.TemplatePackageUrl = packageFolder;
        campaign.TemplateManifestJson = Manifest;
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        campaigns.Update(campaign);

        await uow.SaveChangesAsync(ct);

        return new ImportedDesignResult(template.Id, packageFolder, previewUrl, kind);
    }

    /// <summary>
    /// The <see cref="Template"/> row standing behind an imported design — one per campaign, marked
    /// <see cref="TemplateVisibility.Imported"/>.
    ///
    /// <para>A row rather than a nullable template id, because everything downstream — pinning, the
    /// render, the dashboard, the preview — already resolves a campaign through its template, and a
    /// null would mean auditing every one of those paths for a case that reuses all of them
    /// perfectly well. The visibility is what keeps it out of the gallery: every listing asks for
    /// Public, or Dedicated once used, so this value is invisible to all of them.</para>
    /// </summary>
    private async Task<Template> UpsertTemplateAsync(
        Campaign campaign, string packageFolder, string? previewUrl, CancellationToken ct)
    {
        // Matched on the campaign's OWN slug, never on visibility alone. A campaign could legitimately
        // be pointing at some shared imported row, and "any template marked Imported" would then
        // rewrite that shared row's package for every campaign using it. The slug names exactly one
        // campaign and cannot collide.
        var slug = SlugFor(campaign.Id);
        var existing = await templates.Query(tracking: true)
            .FirstOrDefaultAsync(t => t.Slug == slug, ct);

        if (existing is not null)
        {
            existing.PackageUrl = packageFolder;
            existing.PreviewImageUrl = previewUrl ?? string.Empty;
            existing.ManifestJson = Manifest;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
            templates.Update(existing);
            return existing;
        }

        var template = new Template
        {
            Id = Guid.NewGuid(),
            Name = campaign.Title,
            Slug = slug,
            Description = ImportedDescription,
            Category = "Imported",
            Version = "1.0.0",
            PackageUrl = packageFolder,
            // A zip need contain no image at all, so there is genuinely nothing to preview for some
            // designs. The column is NOT NULL; empty is the honest value for "there isn't one".
            PreviewImageUrl = previewUrl ?? string.Empty,
            ManifestJson = Manifest,
            SceneJson = "{}",
            Visibility = TemplateVisibility.Imported,
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        await templates.AddAsync(template, ct);
        return template;
    }

    /// <summary>
    /// An imported design declares no fields, and that is the whole point: the builder renders
    /// exactly what a template says it has, so an empty manifest is what makes the content step
    /// disappear for a design nobody can edit.
    /// </summary>
    /// <summary>
    /// Never shown anywhere — an imported row is invisible to every gallery read — but the column is
    /// NOT NULL, and a row that cannot be written is a 500 at the end of somebody's upload.
    /// </summary>
    private const string ImportedDescription = "A design the customer brought themselves.";

    /// <summary>One campaign, one imported template row. The id is in the slug so it cannot collide.</summary>
    private static string SlugFor(Guid campaignId) => $"imported-{campaignId:N}";

    private const string Manifest = """{"fields":[],"images":[],"blocks":[],"theme":{}}""";

    private static bool IsVideo(string ext) => ext is ".mp4" or ".webm";

    /// <summary>
    /// The page an uploaded picture becomes.
    ///
    /// <para>Deliberately almost nothing: the design fills the screen and the platform adds no
    /// furniture to it. What it does add is the two things a picture cannot do for itself — a
    /// viewport that fits a phone, and a dark ground so a portrait card on a wide screen sits in
    /// something rather than floating on white.</para>
    ///
    /// <para>The RSVP and the media bucket are NOT written here. The renderer appends both to any
    /// document that lacks its own, which means an imported design and a template get the identical
    /// treatment from one place.</para>
    /// </summary>
    private static string Wrap(string url, string title, bool video)
    {
        var safeTitle = WebUtility.HtmlEncode(title);
        var safeUrl = WebUtility.HtmlEncode(url);

        var media = video
            ? $"""<video class="art" src="{safeUrl}" autoplay muted loop playsinline controls></video>"""
            : $"""<img class="art" src="{safeUrl}" alt="{safeTitle}">""";

        return $"""
            <!doctype html>
            <html lang="en">
            <head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width,initial-scale=1,viewport-fit=cover">
            <title>{safeTitle}</title>
            <style>{Css}</style>
            </head>
            <body>{media}</body>
            </html>
            """;
    }

    /// <summary>
    /// Kept out of the interpolated markup above so the CSS braces stay CSS braces. The custom
    /// properties are named to match what the appended RSVP and media-bucket bars read, so a design
    /// that declares nothing still gets bars that look deliberate rather than bolted on.
    /// </summary>
    private const string Css = """
        :root { --ib-bg:#101014; --ib-text:#f4eef6; --ib-accent:#c9a227; }
        html,body { margin:0; background:var(--ib-bg); color:var(--ib-text); }
        body { min-height:100dvh; display:flex; align-items:center; justify-content:center; }
        /* Contained, not cropped: this is somebody's finished artwork, and taking a corner off it to
           fill a screen would be worse than the letterboxing around it. */
        .art { display:block; max-width:100%; max-height:100dvh; width:auto; height:auto;
               object-fit:contain; }
        """;

    private static string ContentTypeFor(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".html" or ".htm" => "text/html; charset=utf-8",
        ".css" => "text/css; charset=utf-8",
        ".js" => "text/javascript; charset=utf-8",
        ".json" => "application/json",
        ".png" => "image/png",
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".avif" => "image/avif",
        ".svg" => "image/svg+xml",
        ".woff" => "font/woff",
        ".woff2" => "font/woff2",
        ".ttf" => "font/ttf",
        ".otf" => "font/otf",
        ".mp4" => "video/mp4",
        ".webm" => "video/webm",
        _ => "application/octet-stream",
    };
}
