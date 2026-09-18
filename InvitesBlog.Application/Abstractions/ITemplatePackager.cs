namespace InvitesBlog.Application.Abstractions;

/// <summary>A published template package: where it lives and the manifest derived from it.</summary>
public sealed record TemplatePackage(string PackageUrl, string ManifestJson);

/// <summary>
/// Publishes and reads single-file template packages (§template packaging). The designer's publish
/// goes through this port so the Application layer never has to know how the manifest is derived or
/// where packages are stored.
/// </summary>
public interface ITemplatePackager
{
    /// <summary>
    /// Runs the safety scan, then writes the package under <paramref name="basePath"/> (the live
    /// <c>templates/{slug}@{version}</c>).
    /// </summary>
    /// <remarks>
    /// No poster here: the designer's poster is sniffed and stored by <c>DesignService</c> itself, so
    /// only the first-party seeder (which ships posters alongside its templates) passes one, and it
    /// talks to the packager directly.
    /// </remarks>
    Task<TemplatePackage> PublishAsync(
        string basePath, string slug, string version, string html, CancellationToken ct = default);

    /// <summary>The stored package document for a template version, or null when storage doesn't have it.</summary>
    Task<string?> ReadPackageAsync(string slug, string version, CancellationToken ct = default);
}
