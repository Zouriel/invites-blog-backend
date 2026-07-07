using System.Text;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.TemplateCompiler;

namespace InvitesBlog.Infrastructure.Templates;

public sealed record PublishedPackage(string PackageUrl, CompiledTemplatePackage Compiled);

/// <summary>
/// Compiles a <see cref="Scene"/> and publishes its four files to storage under
/// <c>templates/{slug}@{version}/</c> (§5.2), returning the package base URL. This is the single
/// path used by seeding, admin template management, and (later) the designer compile-preview.
/// </summary>
public sealed class TemplatePackagePublisher(IStorageService storage)
{
    private readonly SceneCompiler _compiler = new();

    public async Task<PublishedPackage> PublishAsync(Scene scene, CancellationToken ct = default)
    {
        var pkg = _compiler.Compile(scene);
        var basePath = $"templates/{scene.Slug}@{scene.Version}";

        await storage.PutAsync($"{basePath}/index.html", Bytes(pkg.IndexHtml), "text/html", ct);
        await storage.PutAsync($"{basePath}/styles.css", Bytes(pkg.StylesCss), "text/css", ct);
        await storage.PutAsync($"{basePath}/template.js", Bytes(pkg.TemplateJs), "application/javascript", ct);
        await storage.PutAsync($"{basePath}/manifest.json", Bytes(pkg.ManifestJson), "application/json", ct);

        return new PublishedPackage(storage.PublicUrl($"{basePath}/"), pkg);
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
}
