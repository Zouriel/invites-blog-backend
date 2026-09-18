using InvitesBlog.Application.Abstractions;

namespace InvitesBlog.Infrastructure.Templates;

/// <summary>
/// Exposes <see cref="RawTemplatePackager"/> to the Application layer as <see cref="ITemplatePackager"/>,
/// flattening the compiler's manifest types into plain strings so Application never depends on them.
/// </summary>
public sealed class TemplatePackagerAdapter(RawTemplatePackager packager, IStorageService storage) : ITemplatePackager
{
    public async Task<TemplatePackage> PublishAsync(
        string basePath, string slug, string version, string html, CancellationToken ct = default,
        byte[]? poster = null)
    {
        var published = await packager.PublishToAsync(basePath, slug, version, html, ct: ct, poster: poster);
        return new TemplatePackage(published.PackageUrl, published.ManifestJson, Flatten(published.Manifest), published.PosterUrl);
    }

    public async Task<string?> ReadPackageAsync(string slug, string version, CancellationToken ct = default)
    {
        var bytes = await storage.GetAsync($"templates/{slug}@{version}/index.html", ct);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static TemplateStructure Flatten(TemplateCompiler.TemplateManifest m) => new(
        m.Fields.Select(f => $"{f.Label} ({f.Type})").ToList(),
        m.ImageSlots.Select(s => s.Multiple ? $"{s.Label} (gallery)" : s.Label).ToList(),
        m.Roles.ToList(),
        m.Theme.Keys.Select(k => k.Key).ToList());
}
