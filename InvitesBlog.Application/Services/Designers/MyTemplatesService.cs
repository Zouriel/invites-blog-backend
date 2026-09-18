using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>
/// "My designs": the templates this person published. Ownership is checked on every mutation rather
/// than inferred from which endpoint was called, so a designer can never reach someone else's template
/// by guessing an id (an admin may still act on any template).
/// </summary>
public sealed class MyTemplatesService(
    ICurrentUser currentUser,
    ITemplateRepository templates,
    ICampaignRepository campaigns,
    IUnitOfWork uow) : IMyTemplatesService
{
    public async Task<MyTemplatesPageDto> ListAsync(CancellationToken ct = default)
    {
        var me = currentUser.UserId ?? throw new UnauthorizedException();

        // Only what this person published — an admin too. The platform's whole catalogue is the admin
        // screen's (System templates), not something to wade through under "My designs".
        var rows = await templates.Query()
            .Where(t => t.DesignerUserId == me)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync(ct);
        if (rows.Count == 0) return new MyTemplatesPageDto([]);

        var ids = rows.Select(t => t.Id).ToList();

        // One aggregate in a single round trip, rather than a query per row.
        var usage = await campaigns.Query()
            .Where(c => ids.Contains(c.TemplateId))
            .GroupBy(c => c.TemplateId)
            .Select(g => new { TemplateId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.TemplateId, x => x.Count, ct);

        var list = rows.Select(t => new MyTemplateRowDto(
            t.Id, t.Name, t.Slug, t.Category, t.Version, t.Visibility, t.IsActive,
            StaticPreview(t.PreviewImageUrl), t.DesignerName, t.DesignerUserId,
            usage.GetValueOrDefault(t.Id),
            t.UpdatedAt)).ToList();

        return new MyTemplatesPageDto(list);
    }

    public async Task<DeleteTemplateResultDto> DeleteAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await LoadOwnedAsync(templateId, ct);
        var count = await campaigns.CountAsync(c => c.TemplateId == template.Id, ct);

        // A template made for one customer is promised to them: it's how they reach their invitation,
        // so it outlives the gallery listing exactly like a used one does.
        var madeForSomeone = template.Visibility == TemplateVisibility.Dedicated && template.AssignedEmail is not null;

        if (count > 0 || madeForSomeone)
        {
            // Unlist, never delete: those campaigns still serve the package they pinned, and removing
            // the row would strand invitations that are already in people's inboxes.
            template.IsActive = false;
            template.UpdatedAt = DateTimeOffset.UtcNow;
            templates.Update(template);
            await uow.SaveChangesAsync(ct);

            var why = count > 0
                ? $"is used by {count} invitation{(count == 1 ? "" : "s")}"
                : $"was made for {template.AssignedEmail}";
            return new DeleteTemplateResultDto(false, true, count,
                $"“{template.Name}” {why}, so it was removed from the gallery rather than deleted. Those invitations are unaffected.");
        }

        templates.Remove(template);
        await uow.SaveChangesAsync(ct);
        return new DeleteTemplateResultDto(true, false, 0, $"“{template.Name}” was deleted.");
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
