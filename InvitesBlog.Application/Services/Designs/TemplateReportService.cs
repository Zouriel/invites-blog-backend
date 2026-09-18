using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Designs;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using InvitesBlog.Application.Common;

namespace InvitesBlog.Application.Services.Designs;

public interface ITemplateReportService
{
    Task ReportAsync(Guid templateId, ReportTemplateRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<TemplateReportDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<TemplateReportDto> ResolveAsync(Guid reportId, ResolveReportRequest request, CancellationToken ct = default);
    Task RestorePublishingAsync(Guid userId, CancellationToken ct = default);
    Task RelistAsync(Guid templateId, CancellationToken ct = default);

    /// <summary>
    /// Takes a public template out of the gallery without a report: it becomes private to its creator
    /// (a template made for someone was never public), and the creator can't list it again until an
    /// admin puts it back. Invitations already made with it are untouched.
    /// </summary>
    Task UnpublishAsync(Guid templateId, CancellationToken ct = default);

    /// <summary>Undoes <see cref="UnpublishAsync"/>: back in the gallery, the hold lifted.</summary>
    Task RepublishAsync(Guid templateId, CancellationToken ct = default);
}

/// <summary>
/// The safety net under a gallery that publishes without review. Anyone can report a template; an
/// admin dismisses the report, unlists the template (out of the gallery, owner keeps using it), or
/// removes it (inactive everywhere new, and the owner loses public publishing).
///
/// <para>Neither action touches an invitation that was already made with the template: campaigns pin
/// their package, so a guest who already has one keeps seeing exactly what was sent.</para>
/// </summary>
public sealed class TemplateReportService(
    ICurrentUser currentUser,
    IRepository<TemplateReport> reports,
    IRepository<AppUser> users,
    ITemplateRepository templates,
    IUnitOfWork uow) : ITemplateReportService
{
    public static readonly string[] Reasons = ["offensive", "copyright", "spam", "broken", "other"];

    public async Task ReportAsync(Guid templateId, ReportTemplateRequest request, CancellationToken ct = default)
    {
        var template = await templates.GetByIdAsync(templateId, ct);
        if (template is null || template.Visibility != TemplateVisibility.Public || !template.IsActive)
            throw new NotFoundException("That template isn't in the gallery.", "template_not_found");
        if (!Reasons.Contains(request.Reason))
            throw new BusinessRuleException("Choose what's wrong with this template.", "reason_required");

        var details = request.Details?.Trim();
        if (details is { Length: > 1000 }) details = details[..1000];

        // One open report per person per template is plenty; a second press just updates the note.
        if (currentUser.UserId is { } me)
        {
            var open = await reports.Query(tracking: true).FirstOrDefaultAsync(
                r => r.TemplateId == templateId && r.ReporterUserId == me && r.Status == TemplateReportStatus.Open, ct);
            if (open is not null)
            {
                open.Reason = request.Reason;
                open.Details = details;
                await uow.SaveChangesAsync(ct);
                return;
            }
        }

        await reports.AddAsync(new TemplateReport
        {
            Id = Guid.NewGuid(),
            TemplateId = templateId,
            ReporterUserId = currentUser.UserId,
            Reason = request.Reason,
            Details = string.IsNullOrWhiteSpace(details) ? null : details,
            Status = TemplateReportStatus.Open,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
        await uow.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TemplateReportDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var query = reports.Query();
        query = status switch
        {
            "resolved" => query.Where(r => r.Status == TemplateReportStatus.Resolved),
            "all" => query,
            _ => query.Where(r => r.Status == TemplateReportStatus.Open),
        };
        var rows = await query.OrderBy(r => r.CreatedAt).Take(500).ToListAsync(ct);
        return await ToDtosAsync(rows, ct);
    }

    public async Task<TemplateReportDto> ResolveAsync(Guid reportId, ResolveReportRequest request, CancellationToken ct = default)
    {
        var report = await reports.Query(tracking: true).FirstOrDefaultAsync(r => r.Id == reportId, ct)
                     ?? throw new NotFoundException("That report doesn't exist.", "report_not_found");
        var template = await templates.Query(tracking: true).FirstOrDefaultAsync(t => t.Id == report.TemplateId, ct);
        var now = DateTimeOffset.UtcNow;
        var note = request.Note?.Trim();
        if (note is { Length: > 1000 }) note = note[..1000];

        string resolution;
        switch (request.Action)
        {
            case "dismiss":
                resolution = "dismissed";
                break;
            case "unlist":
                if (template is null) throw new NotFoundException("That template no longer exists.", "template_not_found");
                if (template.Visibility == TemplateVisibility.Public) template.Visibility = TemplateVisibility.Private;
                template.UnlistedByAdminAt = now;
                template.UpdatedAt = now;
                resolution = "unlisted";
                break;
            case "remove":
                if (template is null) throw new NotFoundException("That template no longer exists.", "template_not_found");
                if (template.Visibility == TemplateVisibility.Public) template.Visibility = TemplateVisibility.Private;
                template.UnlistedByAdminAt = now;
                // Inactive: nobody starts anything new with it. Pinned campaigns still render.
                template.IsActive = false;
                template.UpdatedAt = now;
                if (template.DesignerUserId is { } ownerId
                    && await users.Query(tracking: true).FirstOrDefaultAsync(u => u.Id == ownerId, ct) is { } owner)
                    owner.PublicPublishingRevokedAt ??= now;
                resolution = "removed";
                break;
            default:
                throw new BusinessRuleException("Choose dismiss, unlist or remove.", "action_required");
        }

        // Acting on a template settles every open report about it, not just this one.
        var siblings = request.Action == "dismiss"
            ? [report]
            : await reports.Query(tracking: true)
                .Where(r => r.TemplateId == report.TemplateId && r.Status == TemplateReportStatus.Open)
                .ToListAsync(ct);
        if (!siblings.Contains(report)) siblings.Add(report);
        foreach (var r in siblings)
        {
            r.Status = TemplateReportStatus.Resolved;
            r.Resolution = resolution;
            r.ResolutionNote = note;
            r.ResolvedAt = now;
            r.ResolvedByUserId = currentUser.UserId;
        }

        await uow.SaveChangesAsync(ct);
        return (await ToDtosAsync([report], ct))[0];
    }

    public async Task RestorePublishingAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await users.Query(tracking: true).FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new NotFoundException("That account doesn't exist.", "user_not_found");
        user.PublicPublishingRevokedAt = null;
        await uow.SaveChangesAsync(ct);
    }

    public async Task RelistAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await templates.Query(tracking: true).FirstOrDefaultAsync(t => t.Id == templateId, ct)
                       ?? throw new NotFoundException("That template doesn't exist.", "template_not_found");
        // Clears the admin hold only. Whether it goes back in the gallery is still the owner's call.
        template.UnlistedByAdminAt = null;
        template.IsActive = true;
        template.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    public async Task UnpublishAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await templates.Query(tracking: true).FirstOrDefaultAsync(t => t.Id == templateId, ct)
                       ?? throw new NotFoundException("That template doesn't exist.", "template_not_found");
        if (template.Visibility != TemplateVisibility.Public)
            throw new BusinessRuleException("Only a template in the gallery can be unpublished.", "not_public");
        var now = DateTimeOffset.UtcNow;
        template.Visibility = TemplateVisibility.Private;
        template.UnlistedByAdminAt = now;
        template.UpdatedAt = now;
        await uow.SaveChangesAsync(ct);
    }

    public async Task RepublishAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await templates.Query(tracking: true).FirstOrDefaultAsync(t => t.Id == templateId, ct)
                       ?? throw new NotFoundException("That template doesn't exist.", "template_not_found");
        if (template.Visibility != TemplateVisibility.Private || template.AssignedEmail is not null)
            throw new BusinessRuleException("Only a template an admin unpublished can go back in the gallery here.", "not_unpublished");
        template.Visibility = TemplateVisibility.Public;
        template.UnlistedByAdminAt = null;
        template.IsActive = true;
        template.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<TemplateReportDto>> ToDtosAsync(IReadOnlyList<TemplateReport> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.TemplateId).Distinct().ToList();
        var byId = await templates.Query().Where(t => ids.Contains(t.Id)).ToDictionaryAsync(t => t.Id, ct);
        var counts = await reports.Query()
            .Where(r => ids.Contains(r.TemplateId))
            .GroupBy(r => r.TemplateId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return rows.Select(r =>
        {
            byId.TryGetValue(r.TemplateId, out var t);
            var preview = TemplatePoster.OrNull(t?.PreviewImageUrl);
            return new TemplateReportDto(
                r.Id, r.TemplateId, t?.Name ?? "(deleted template)", t?.Slug ?? string.Empty, preview,
                t?.Visibility ?? string.Empty, t?.IsActive ?? false, t?.DesignerName, t?.DesignerUserId,
                r.Reason, r.Details, r.Status.ToString(), r.Resolution, r.ResolutionNote,
                counts.GetValueOrDefault(r.TemplateId), r.CreatedAt, r.ResolvedAt);
        }).ToList();
    }
}
