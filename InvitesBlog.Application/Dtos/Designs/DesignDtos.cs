using System.Text.Json;
using InvitesBlog.Application.Abstractions;

namespace InvitesBlog.Application.Dtos.Designs;

/// <summary>The template a design publishes to, as far as its owner needs to know.</summary>
public sealed record DesignTemplateDto(
    Guid Id,
    string Name,
    string Slug,
    string Version,
    string Visibility,
    bool IsActive,
    string Category,
    string Description,
    string? PreviewImageUrl,
    bool UnlistedByAdmin,
    int EventsUsing,
    int EventsOnOlderVersions,
    string? AssignedEmail = null);

public sealed record DesignSummaryDto(
    Guid Id,
    string Name,
    int Revision,
    int? PublishedRevision,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? LastPublishedAt,
    DesignTemplateDto? Template);

public sealed record DesignPublishDto(string Version, string Visibility, int Revision, int Bytes, DateTimeOffset PublishedAt);

/// <param name="PublicBlockedReason">Why this account can't publish to the gallery right now, or null.</param>
public sealed record DesignDto(
    Guid Id,
    string Name,
    JsonElement Scene,
    int Revision,
    int? PublishedRevision,
    DateTimeOffset UpdatedAt,
    Guid? CampaignId,
    DesignTemplateDto? Template,
    IReadOnlyList<DesignPublishDto> History,
    string? PublicBlockedReason,
    int PublicPublishesLeftToday);

/// <param name="Starter">A starter id; ignored when <paramref name="Scene"/> or <paramref name="FromTemplateId"/> is given.</param>
/// <param name="FromTemplateId">Edit an existing template. A designed one opens exactly; a hand-written one needs <paramref name="Scene"/> from the importer.</param>
/// <param name="Scene">A scene built by the importer.</param>
/// <param name="CampaignId">The event this is being designed for.</param>
public sealed record CreateDesignRequest(
    string? Name,
    string? Starter,
    Guid? FromTemplateId,
    JsonElement? Scene,
    Guid? CampaignId);

public sealed record SaveDesignRequest(JsonElement Scene, string? Name, int BaseRevision);

public sealed record SavedDesignDto(int Revision, DateTimeOffset UpdatedAt);

public sealed record DesignPreviewRequest(
    JsonElement Scene,
    string? Sample,
    IReadOnlyList<string>? Blocks,
    IReadOnlyList<string>? Hidden,
    double? Scroll,
    bool? Editor);

public sealed record DesignPreviewDto(
    string Html,
    int Bytes,
    IReadOnlyList<DesignIssueDto> Issues,
    TemplateStructure Structure,
    bool CanPublish);

/// <param name="Visibility">Private, Person or Public. A dedicated (commissioned) template stays dedicated.</param>
/// <param name="Category">A template type name.</param>
/// <param name="Revision">The revision the person reviewed; a newer save since then is refused.</param>
public sealed record PublishDesignRequest(
    string Visibility,
    string Name,
    string Category,
    string? Description,
    Guid? CampaignId,
    int Revision,
    byte[]? Poster,
    string? PosterContentType,
    string? AssignedEmail = null);

public sealed record PublishResultDto(
    DesignDto Design,
    Guid TemplateId,
    string Slug,
    string Version,
    string Visibility,
    IReadOnlyList<DesignIssueDto> Warnings,
    Guid? AttachedCampaignId);

public sealed record DesignEventDto(
    Guid CampaignId,
    string Title,
    string Version,
    bool IsLatest,
    string Status,
    DateTimeOffset? EventStartAt);

/// <param name="Designed">True when the template was made in the designer and opens exactly — no import needed.</param>
/// <param name="Html">For a hand-written template: the document the importer runs in the browser.</param>
/// <param name="ExistingDesignId">The caller already has a design editing this template.</param>
public sealed record DesignImportSourceDto(
    Guid TemplateId,
    string Name,
    string Version,
    bool Designed,
    string? Html,
    Guid? ExistingDesignId);

public sealed record SetDesignVisibilityRequest(string Visibility);

public sealed record ReportTemplateRequest(string Reason, string? Details);

public sealed record TemplateReportDto(
    Guid Id,
    Guid TemplateId,
    string TemplateName,
    string TemplateSlug,
    string? TemplatePreviewUrl,
    string TemplateVisibility,
    bool TemplateActive,
    string? DesignerName,
    Guid? DesignerUserId,
    string Reason,
    string? Details,
    string Status,
    string? Resolution,
    string? ResolutionNote,
    int ReportsForTemplate,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt);

/// <param name="Action">dismiss | unlist | remove.</param>
public sealed record ResolveReportRequest(string Action, string? Note);
