using System.Text;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>
/// One screen, scoped by role: an admin manages every template on the platform, a designer manages
/// the ones they authored. Ownership is checked on every mutation rather than inferred from which
/// endpoint was called, so a designer can never reach someone else's template by guessing an id.
/// </summary>
public sealed class MyTemplatesService(
    ICurrentUser currentUser,
    ITemplateRepository templates,
    ICampaignRepository campaigns,
    IRepository<CustomTemplate> submissions,
    IRepository<Inquiry> inquiries,
    IStorageService storage,
    IUnitOfWork uow) : IMyTemplatesService
{
    public async Task<MyTemplatesPageDto> ListAsync(CancellationToken ct = default)
    {
        var isAdmin = IsAdmin();
        var me = currentUser.UserId ?? throw new UnauthorizedException();

        var query = templates.Query();
        if (!isAdmin) query = query.Where(t => t.DesignerUserId == me);

        var rows = await query.OrderByDescending(t => t.UpdatedAt).ToListAsync(ct);
        if (rows.Count == 0)
            return new MyTemplatesPageDto(isAdmin ? "system" : "mine",
                isAdmin ? "System templates" : "My templates", []);

        var ids = rows.Select(t => t.Id).ToList();

        // Two aggregates in one round trip each, rather than a query per row.
        var usage = await campaigns.Query()
            .Where(c => ids.Contains(c.TemplateId))
            .GroupBy(c => c.TemplateId)
            .Select(g => new { TemplateId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TemplateId, x => x.Count, ct);

        var pending = await submissions.Query()
            .Where(s => s.PublishedTemplateId != null
                        && ids.Contains(s.PublishedTemplateId!.Value)
                        && (s.Status == CustomTemplateStatus.Submitted || s.Status == CustomTemplateStatus.InReview))
            .Select(s => s.PublishedTemplateId!.Value)
            .Distinct()
            .ToListAsync(ct);
        var pendingSet = pending.ToHashSet();

        var list = rows.Select(t => new MyTemplateRowDto(
            t.Id, t.Name, t.Slug, t.Category, t.Version, t.Visibility, t.IsActive,
            StaticPreview(t.PreviewImageUrl), t.DesignerName, t.DesignerUserId,
            t.UsagePrice, t.CommissionPrice,
            usage.GetValueOrDefault(t.Id),
            CanEditDirectly: isAdmin,
            PendingReview: pendingSet.Contains(t.Id),
            t.UpdatedAt)).ToList();

        return new MyTemplatesPageDto(
            isAdmin ? "system" : "mine",
            isAdmin ? "System templates" : "My templates",
            list);
    }

    public async Task<MyTemplateRowDto> SetPricingAsync(
        Guid templateId, SetTemplatePricingRequest request, CancellationToken ct = default)
    {
        var template = await LoadOwnedAsync(templateId, ct);

        if (request.UsagePrice is < 0 || request.CommissionPrice is < 0)
            throw new BusinessRuleException("A price can't be negative.", "invalid_price");

        template.UsagePrice = request.UsagePrice;

        // The commission is what the platform agreed to pay for bespoke work — a designer doesn't get
        // to set that themselves.
        if (IsAdmin()) template.CommissionPrice = request.CommissionPrice;

        template.UpdatedAt = DateTimeOffset.UtcNow;
        templates.Update(template);
        await uow.SaveChangesAsync(ct);

        var count = await campaigns.CountAsync(c => c.TemplateId == template.Id, ct);

        // Report the review state rather than assuming none: a price change doesn't clear a revision
        // that is still waiting, and the row would lose its badge until the next full load.
        var pendingReview = await submissions.Query()
            .AnyAsync(sub => sub.PublishedTemplateId == template.Id
                             && (sub.Status == CustomTemplateStatus.Submitted
                                 || sub.Status == CustomTemplateStatus.InReview), ct);

        return new MyTemplateRowDto(
            template.Id, template.Name, template.Slug, template.Category, template.Version,
            template.Visibility, template.IsActive, StaticPreview(template.PreviewImageUrl),
            template.DesignerName, template.DesignerUserId, template.UsagePrice, template.CommissionPrice,
            count, IsAdmin(), pendingReview, template.UpdatedAt);
    }

    public async Task<DeleteTemplateResultDto> DeleteAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await LoadOwnedAsync(templateId, ct);
        var count = await campaigns.CountAsync(c => c.TemplateId == template.Id, ct);

        // A template issued to a customer is promised to them: their "your invitation is ready" link
        // resolves through this row, so it outlives the gallery listing exactly like a used one does.
        var issued = await inquiries.Query()
            .CountAsync(i => i.IssuedTemplateId == template.Id, ct);

        if (count > 0 || issued > 0)
        {
            // Unlist, never delete: those campaigns still serve the package they pinned, and removing
            // the row would strand invitations that are already in people's inboxes.
            template.IsActive = false;
            template.UpdatedAt = DateTimeOffset.UtcNow;
            templates.Update(template);
            await uow.SaveChangesAsync(ct);

            var why = count > 0
                ? $"is used by {count} invitation{(count == 1 ? "" : "s")}"
                : $"was issued to {issued} customer{(issued == 1 ? "" : "s")}";
            return new DeleteTemplateResultDto(false, true, count,
                $"“{template.Name}” {why}, so it was removed from the gallery rather than deleted. Those invitations are unaffected.");
        }

        // Nothing points at it any more except the review row it was published from. Cut that link
        // first — there are no foreign keys here, so a left-behind id would point at a ghost and the
        // designer's dashboard would offer to revise a template that no longer exists.
        var published = await submissions.Query(tracking: true)
            .Where(s => s.PublishedTemplateId == template.Id)
            .ToListAsync(ct);
        foreach (var submission in published) submission.PublishedTemplateId = null;

        templates.Remove(template);
        await uow.SaveChangesAsync(ct);
        return new DeleteTemplateResultDto(true, false, 0, $"“{template.Name}” was deleted.");
    }

    public async Task<string> GetSourceAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await LoadOwnedAsync(templateId, ct);

        // Prefer the verbatim submission we kept; fall back to the stored package for templates that
        // predate the review pipeline (the platform's own).
        var submission = await submissions.Query()
            .Where(s => s.PublishedTemplateId == template.Id && s.Html != "")
            .OrderByDescending(s => s.UpdatedAt)
            .FirstOrDefaultAsync(ct);
        if (submission is not null) return submission.Html;

        var bytes = await storage.GetAsync($"templates/{template.Slug}@{template.Version}/index.html", ct)
                    ?? throw new NotFoundException("That template's source isn't available.", "source_not_found");
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>Loads a template the caller is actually allowed to touch.</summary>
    private async Task<Template> LoadOwnedAsync(Guid id, CancellationToken ct)
    {
        var template = await templates.Query(tracking: true).FirstOrDefaultAsync(t => t.Id == id, ct)
                       ?? throw new NotFoundException("That template doesn't exist.", "template_not_found");

        if (IsAdmin()) return template;

        var me = currentUser.UserId ?? throw new UnauthorizedException();
        if (template.DesignerUserId != me)
            throw new ForbiddenException("That isn't your template.", "not_your_template");

        return template;
    }

    private bool IsAdmin() => currentUser.HasPermission(Permissions.Templates.Manage);

    /// <summary>
    /// Older templates store a pointer to their live page here instead of a real image; the table
    /// shows a placeholder rather than trying to load a whole invitation as a thumbnail.
    /// </summary>
    private static string? StaticPreview(string? url) =>
        string.IsNullOrWhiteSpace(url) || url.EndsWith(".html", StringComparison.OrdinalIgnoreCase) || url.EndsWith('/')
            ? null
            : url;
}
