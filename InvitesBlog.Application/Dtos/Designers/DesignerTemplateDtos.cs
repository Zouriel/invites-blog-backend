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

/// <summary>The templates the signed-in person published (admins included: the full catalogue is the admin screen's).</summary>
public sealed record MyTemplatesPageDto(IReadOnlyList<MyTemplateRowDto> Templates);

/// <summary>What a delete actually did — unlisting is not the same as removing.</summary>
public sealed record DeleteTemplateResultDto(bool Deleted, bool Unlisted, int CampaignCount, string Message);
