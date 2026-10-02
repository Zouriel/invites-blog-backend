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
    /// <summary>Whether their Premium subscription is in force, and when it ends (null: no end, or never had one).</summary>
    bool PremiumActive = false,
    DateTimeOffset? PremiumEndsAt = null,
    /// <summary>Templates they published FOR someone: their clients.</summary>
    int ClientTemplates = 0);

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

/// <summary>
/// One event made from a designer's template: who, and when. The host is named only where the
/// designer has a right to know — a design made for someone (their client) or kept private (their
/// own) — or to an admin; a stranger who picked a public design stays "a host".
/// </summary>
/// <param name="Status">"Not finished", "Live" or "Cancelled".</param>
public sealed record TemplateUseDto(
    Guid CampaignId, string EventTitle, DateTimeOffset UsedAt, DateTimeOffset EventStartAt, string Status,
    string Kind, string? HostName, string? HostEmail, string Pass);

/// <summary>What a delete actually did — unlisting is not the same as removing.</summary>
public sealed record DeleteTemplateResultDto(bool Deleted, bool Unlisted, int CampaignCount, string Message);
