namespace InvitesBlog.Application.Abstractions;

/// <summary>What a template declares, flattened to plain strings for the layers above.</summary>
public sealed record TemplateStructure(
    IReadOnlyList<string> Fields,
    IReadOnlyList<string> ImageSlots,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> ThemeKeys);

/// <summary>A published template package: where it lives and the manifest derived from it.</summary>
public sealed record TemplatePackage(string PackageUrl, string ManifestJson, TemplateStructure Structure);

/// <summary>
/// Scans, describes and publishes single-file templates (§template packaging). The Application layer
/// drives the submission/review pipeline through this port so it never has to know how the manifest is
/// derived or where packages are stored.
/// </summary>
public interface ITemplatePackager
{
    /// <summary>The soft size budget shown to designers (bytes).</summary>
    int RecommendedBytes { get; }
    /// <summary>The hard size ceiling a submission may not exceed (bytes).</summary>
    int MaxBytes { get; }

    /// <summary>
    /// Runs the safety + authoring scan. Throws the specific business-rule failure on a violation and
    /// returns normally when the template is acceptable.
    /// </summary>
    void Scan(string html);

    /// <summary>The template's declared structure, for the reviewer's non-technical summary view.</summary>
    TemplateStructure Describe(string slug, string html);

    /// <summary>
    /// Scans, then writes the package under <paramref name="basePath"/>. Submissions stage under
    /// <c>submissions/{id}</c>; only approval publishes to the live <c>templates/{slug}@{version}</c>.
    /// </summary>
    Task<TemplatePackage> PublishAsync(
        string basePath, string slug, string version, string html, CancellationToken ct = default);
}
