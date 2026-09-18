using InvitesBlog.Application.Abstractions;

namespace InvitesBlog.Infrastructure.Templates;

/// <summary>
/// Exposes <see cref="RawTemplatePackager"/> to the Application layer as <see cref="ITemplatePackager"/>,
/// handing back only the package URL and manifest JSON so Application never depends on the compiler's types.
/// </summary>
public sealed class TemplatePackagerAdapter(RawTemplatePackager packager, IStorageService storage) : ITemplatePackager
{
    public async Task<TemplatePackage> PublishAsync(
        string basePath, string slug, string version, string html, CancellationToken ct = default)
    {
        var published = await packager.PublishToAsync(basePath, slug, version, html, ct: ct);
        return new TemplatePackage(published.PackageUrl, published.ManifestJson);
    }

    public async Task<string?> ReadPackageAsync(string slug, string version, CancellationToken ct = default)
    {
        var bytes = await storage.GetAsync($"templates/{slug}@{version}/index.html", ct);
        return bytes is null ? null : System.Text.Encoding.UTF8.GetString(bytes);
    }
}
