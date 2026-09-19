namespace InvitesBlog.Application.Dtos.Templates;

/// <summary>A gallery card.</summary>
/// <param name="PreviewImageUrl">
/// The poster image, or null when the template has none (the card shows its own fallback). Never a
/// page: see <see cref="Common.TemplatePoster"/>.
/// </param>
public sealed record TemplateListItemDto(
    Guid Id, string Name, string Slug, string Category, string Description,
    string? PreviewImageUrl, string? PreviewAnimationUrl,
    string? DesignerName, string PackageUrl, string Version, bool IsShowcase);

/// <summary>A template's detail page.</summary>
/// <param name="PreviewImageUrl">The poster image, or null — never a page (<see cref="Common.TemplatePoster"/>).</param>
public sealed record TemplateDetailDto(
    Guid Id, string Name, string Slug, string Category, string Description, string Version,
    string? PreviewImageUrl, string? PreviewAnimationUrl,
    string? DesignerName, string PackageUrl, string ManifestJson, bool IsShowcase);
