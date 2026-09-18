namespace InvitesBlog.Application.Dtos.Designers;

/// <summary>A designer as the admin list shows them.</summary>
public sealed record DesignerAdminDto(
    Guid UserId,
    /// <summary>Null for an account that only ever signed in with a phone number.</summary>
    string? Email,
    string DisplayName,
    bool IsActive,
    IReadOnlyList<string> LinkedProviders,
    int PublishedTemplates,
    DateTimeOffset JoinedAt);

/// <summary>One row of the templates table.</summary>
public sealed record MyTemplateRowDto(
    Guid Id,
    string Name,
    string Slug,
    string Category,
    string Version,
    string Visibility,
    bool IsActive,
    string? PreviewImageUrl,
    string? DesignerName,
    Guid? DesignerUserId,
    int CampaignCount,
    DateTimeOffset UpdatedAt);

/// <summary>The table plus the context the screen needs to title and explain itself.</summary>
/// <param name="Scope">"system" when an admin is looking at everything, "mine" for a designer's own.</param>
public sealed record MyTemplatesPageDto(
    string Scope, string Title, IReadOnlyList<MyTemplateRowDto> Templates);

/// <summary>What a delete actually did — unlisting is not the same as removing.</summary>
public sealed record DeleteTemplateResultDto(bool Deleted, bool Unlisted, int CampaignCount, string Message);
