using System.Text;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.TemplateCompiler;

namespace InvitesBlog.Infrastructure.Templates;

public sealed record PublishedPackage(string PackageUrl, CompiledTemplatePackage Compiled);

/// <summary>
/// Compiles a <see cref="Scene"/> and publishes it as a SINGLE self-contained <c>index.html</c> (the
/// CSS and injector inlined) plus <c>manifest.json</c> under <c>templates/{slug}@{version}/</c>. One
/// pipeline for seeding and admin/designer template management; every template is one file.
/// </summary>
public sealed class TemplatePackagePublisher(IStorageService storage)
{
    private readonly SceneCompiler _compiler = new();

    public async Task<PublishedPackage> PublishAsync(Scene scene, CancellationToken ct = default)
    {
        var pkg = _compiler.Compile(scene);
        var basePath = $"templates/{scene.Slug}@{scene.Version}";

        var singleFile = pkg.IndexHtml
            .Replace("<link rel=\"stylesheet\" href=\"styles.css\">", $"<style>{pkg.StylesCss}</style>")
            .Replace("<script src=\"template.js\"></script>", $"<script>{pkg.TemplateJs}</script>");

        await storage.PutAsync($"{basePath}/index.html", Bytes(singleFile), "text/html", ct);
        await storage.PutAsync($"{basePath}/manifest.json", Bytes(pkg.ManifestJson), "application/json", ct);

        return new PublishedPackage(storage.PublicUrl($"{basePath}/"), pkg);
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
}
