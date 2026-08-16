using InvitesBlog.Application.Abstractions;

namespace InvitesBlog.Infrastructure.Templates;

/// <summary>
/// Exposes <see cref="RawTemplatePackager"/> to the Application layer as <see cref="ITemplatePackager"/>,
/// flattening the compiler's manifest types into plain strings so Application never depends on them.
/// </summary>
public sealed class TemplatePackagerAdapter(RawTemplatePackager packager) : ITemplatePackager
{
    public int RecommendedBytes => RawTemplatePackager.RecommendedTemplateBytes;
    public int MaxBytes => RawTemplatePackager.MaxTemplateBytes;

    public void Scan(string html) => RawTemplatePackager.EnsureSelfContainedAndSafe(html);

    public TemplateStructure Describe(string slug, string html)
    {
        var manifest = packager.BuildManifest(slug, "scan", html);
        return Flatten(manifest);
    }

    public async Task<TemplatePackage> PublishAsync(
        string basePath, string slug, string version, string html, CancellationToken ct = default)
    {
        var published = await packager.PublishToAsync(basePath, slug, version, html, ct);
        return new TemplatePackage(published.PackageUrl, published.ManifestJson, Flatten(published.Manifest));
    }

    private static TemplateStructure Flatten(TemplateCompiler.TemplateManifest m) => new(
        m.Fields.Select(f => $"{f.Label} ({f.Type})").ToList(),
        m.ImageSlots.Select(s => s.Multiple ? $"{s.Label} (gallery)" : s.Label).ToList(),
        m.Roles.ToList(),
        m.Theme.Keys.Select(k => k.Key).ToList());
}
