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
    DateTimeOffset JoinedAt,
    /// <summary>Whether their Studio plan is in force, and when it ends (null: no end, or never had one).</summary>
    bool StudioActive = false,
    DateTimeOffset? StudioEndsAt = null,
    /// <summary>Passes they hold and haven't given to a client yet.</summary>
    int PassCredits = 0,
    /// <summary>Templates they published FOR someone: their clients.</summary>
    int ClientTemplates = 0,
    /// <summary>The passes they hold, by kind.</summary>
    int PartyCredits = 0,
    int WeddingCredits = 0);

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
