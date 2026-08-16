using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Filters.Designers;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>
/// The admin review queue (§4.2). Approval here is the ONLY place a submission becomes a real gallery
/// <see cref="Template"/> — and the only place <see cref="Template.Version"/> is bumped and a fresh
/// manifest generated. Editing an already-published template goes through the same queue, so a live
/// template is never mutated without a review.
/// </summary>
public sealed class TemplateReviewService(
    IRepository<CustomTemplate> submissions,
    IRepository<AppUser> users,
    ITemplateRepository templates,
    ITemplatePackager packager,
    IUnitOfWork uow) : ITemplateReviewService
{
    public async Task<PagedResult<TemplateSubmissionDto>> ListAsync(
        TemplateSubmissionFilter filter, CancellationToken ct = default)
    {
        var query = submissions.Query();

        if (!string.IsNullOrWhiteSpace(filter.Status))
        {
            if (!Enum.TryParse<CustomTemplateStatus>(filter.Status, ignoreCase: true, out var status))
                throw new BusinessRuleException($"Unknown status '{filter.Status}'.", "unknown_submission_status");
            query = query.Where(t => t.Status == status);
        }
        if (filter.DesignerUserId is { } designerId)
            query = query.Where(t => t.DesignerUserId == designerId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(t => t.Name.ToLower().Contains(term) || t.Slug.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var page = await query
            // Oldest first — the review queue is a queue.
            .OrderBy(t => t.CreatedAt)
            .Skip(filter.Skip).Take(filter.PageSize)
            .ToListAsync(ct);

        return PagedResult<TemplateSubmissionDto>.Create(await ToDtosAsync(page, ct), total, filter);
    }

    public async Task<TemplateSubmissionDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await LoadAsync(id, ct);
        return (await ToDtosAsync([entity], ct))[0];
    }

    public async Task<TemplateSubmissionDto> ReviewAsync(
        Guid id, ReviewSubmissionRequest request, CancellationToken ct = default)
    {
        var entity = await LoadAsync(id, ct);

        if (entity.Status is CustomTemplateStatus.Published or CustomTemplateStatus.Approved)
            throw new InvalidStateException("That submission has already been approved.", "submission_already_reviewed");

        if (!request.Approve)
        {
            if (string.IsNullOrWhiteSpace(request.RejectionReason))
                throw new BusinessRuleException(
                    "Tell the designer why it was rejected so they can fix it.", "rejection_reason_required");

            entity.Status = CustomTemplateStatus.Rejected;
            entity.RejectionReason = request.RejectionReason.Trim();
        }
        else
        {
            await PromoteAsync(entity, ct);
            entity.Status = CustomTemplateStatus.Published;
            entity.RejectionReason = null;
        }

        entity.UpdatedAt = DateTimeOffset.UtcNow;
        submissions.Update(entity);
        await uow.SaveChangesAsync(ct);

        return (await ToDtosAsync([entity], ct))[0];
    }

    /// <summary>
    /// Publishes the approved source to the live template path and either creates the gallery template
    /// or bumps the version of the one this submission edits. The previous version's stored package is
    /// left untouched, so any campaign still pinned to it renders byte-for-byte as before.
    /// </summary>
    private async Task PromoteAsync(CustomTemplate entity, CancellationToken ct)
    {
        var existing = entity.PublishedTemplateId is { } publishedId
            ? await templates.GetByIdAsync(publishedId, ct)
            : null;

        var slug = existing?.Slug ?? entity.Slug;
        var version = existing is null ? "1.0.0" : NextVersion(existing.Version);
        var package = await packager.PublishAsync($"templates/{slug}@{version}", slug, version, entity.Html, ct);
        var now = DateTimeOffset.UtcNow;

        if (existing is not null)
        {
            existing.Name = entity.Name;
            existing.Category = entity.Category;
            existing.Description = entity.Description;
            existing.Version = version;
            existing.ManifestJson = package.ManifestJson;
            existing.PackageUrl = package.PackageUrl;
            if (entity.PreviewImageUrl is { Length: > 0 } preview) existing.PreviewImageUrl = preview;
            existing.CommissionPrice = entity.CommissionPrice;
            existing.UsagePrice = entity.UsagePrice;
            existing.IsActive = true;
            existing.UpdatedAt = now;
            templates.Update(existing);
            return;
        }

        // A commissioned template starts private to the person who paid for it — the existing dedicated
        // mechanism — and only reaches the public gallery once both parties consent (§Phase 5).
        var commissioned = !string.IsNullOrWhiteSpace(entity.RequestedByEmail);
        var designer = await users.GetByIdAsync(entity.DesignerUserId, ct);

        var template = new Template
        {
            Id = Guid.NewGuid(),
            Name = entity.Name,
            Slug = slug,
            Version = version,
            Category = entity.Category,
            Description = string.IsNullOrWhiteSpace(entity.Description)
                ? $"A {entity.Category.ToLowerInvariant()} invitation template."
                : entity.Description,
            PreviewImageUrl = entity.PreviewImageUrl ?? $"{package.PackageUrl}index.html",
            IsPremium = false,
            DesignerUserId = entity.DesignerUserId,
            DesignerName = designer?.DisplayName ?? "Community designer",
            SceneJson = "{}",
            ManifestJson = package.ManifestJson,
            PackageUrl = package.PackageUrl,
            IsActive = true,
            Visibility = commissioned ? TemplateVisibility.Dedicated : TemplateVisibility.Public,
            AssignedEmail = entity.RequestedByEmail,
            RequestedByEmail = entity.RequestedByEmail,
            CommissionPrice = entity.CommissionPrice,
            UsagePrice = entity.UsagePrice,
            RequesterConsentToPublish = entity.RequesterConsentToPublish,
            DesignerConsentToPublish = entity.DesignerConsentToPublish,
            CreatedAt = now,
            UpdatedAt = now
        };
        await templates.AddAsync(template, ct);
        entity.PublishedTemplateId = template.Id;
    }

    /// <summary>Bumps the patch component: <c>1.0.0</c> → <c>1.0.1</c>. Anything unparseable restarts at 1.0.1.</summary>
    public static string NextVersion(string current)
    {
        var parts = (current ?? string.Empty).Split('.');
        if (parts.Length == 3 && int.TryParse(parts[2], out var patch))
            return $"{parts[0]}.{parts[1]}.{patch + 1}";
        return "1.0.1";
    }

    private async Task<CustomTemplate> LoadAsync(Guid id, CancellationToken ct) =>
        await submissions.Query(tracking: true).FirstOrDefaultAsync(t => t.Id == id, ct)
        ?? throw new NotFoundException("That submission doesn't exist.", "submission_not_found");

    private async Task<IReadOnlyList<TemplateSubmissionDto>> ToDtosAsync(
        IReadOnlyList<CustomTemplate> entities, CancellationToken ct)
    {
        var designerIds = entities.Select(e => e.DesignerUserId).Distinct().ToList();
        var designers = await users.Query()
            .Where(u => designerIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, ct);

        return entities.Select(e =>
        {
            designers.TryGetValue(e.DesignerUserId, out var designer);
            return new TemplateSubmissionDto(
                DesignerTemplateService.ToDto(e),
                e.DesignerUserId,
                designer?.Email ?? "(deleted account)",
                designer?.DisplayName ?? "(deleted account)",
                e.Html);
        }).ToList();
    }
}
