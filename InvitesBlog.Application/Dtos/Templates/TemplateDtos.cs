namespace InvitesBlog.Application.Dtos.Templates;

public sealed record TemplateListItemDto(
    Guid Id, string Name, string Slug, string Category, string Description,
    string PreviewImageUrl, string? PreviewAnimationUrl, bool IsPremium,
    string? DesignerName, string PackageUrl, string Version);

public sealed record TemplateDetailDto(
    Guid Id, string Name, string Slug, string Category, string Description, string Version,
    string PreviewImageUrl, string? PreviewAnimationUrl, bool IsPremium,
    string? DesignerName, string PackageUrl, string ManifestJson);
