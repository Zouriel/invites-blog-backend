using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Filters.Templates;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// Admin management of the platform's templates: the list, and removing one. Templates are made in the
/// designer and published from there — there is no upload path.
/// </summary>
[Route("api/admin/templates")]
public sealed class AdminTemplatesController(
    ITemplateRepository templates,
    ICampaignRepository campaigns,
    IMyTemplatesService removal) : BaseApiController
{
    /// <summary>An admin management row — every template (incl. inactive/dedicated) plus how many
    /// campaigns already use it, so the admin knows whether a delete will hard-delete or deactivate.</summary>
    /// <param name="PreviewImageUrl">The gallery poster, or null when there's none (the card then shows the live page).</param>
    /// <param name="DesignerName">Who published it; null for the platform's own templates.</param>
    public sealed record AdminTemplateDto(
        Guid Id, string Name, string Slug, string Category, string Version, string PackageUrl,
        string Visibility, bool IsActive, string? AssignedEmail, int CampaignCount,
        string? PreviewImageUrl, string? DesignerName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
        DateTimeOffset? UnlistedByAdminAt);

    public sealed record DeleteResultDto(bool Deleted, bool Deactivated, int CampaignCount);

    /// <summary>
    /// GET /api/admin/templates — the management list (newest first). Paged, searchable (name/slug),
    /// filterable by category, and split by <c>status</c> tab: <c>active</c> (default), <c>inactive</c>, <c>all</c>.
    /// </summary>
    [HttpGet]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> List([FromQuery] AdminTemplateFilter filter, CancellationToken ct)
    {
        // Templates only. An Imported row is one event's own design (or an event with nothing to render,
        // just its photos) — it belongs to that event, isn't a template anyone can pick, and has no
        // package worth previewing here.
        var query = templates.Query().Where(t => t.Visibility != TemplateVisibility.Imported);

        query = filter.Status?.Trim().ToLowerInvariant() switch
        {
            "inactive" => query.Where(t => !t.IsActive),
            "all" => query,
            _ => query.Where(t => t.IsActive), // default: active only
        };
        if (string.Equals(filter.Visibility?.Trim(), "public", StringComparison.OrdinalIgnoreCase))
            query = query.Where(t => t.Visibility == TemplateVisibility.Public);
        if (!string.IsNullOrWhiteSpace(filter.Category))
            query = query.Where(t => t.Category == filter.Category);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            query = query.Where(t => t.Name.ToLower().Contains(term) || t.Slug.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var page = await query
            // Latest first: whatever was published or changed most recently leads.
            .OrderByDescending(t => t.UpdatedAt).ThenByDescending(t => t.CreatedAt)
            .Skip(filter.Skip).Take(filter.PageSize)
            .ToListAsync(ct);

        var items = new List<AdminTemplateDto>(page.Count);
        foreach (var t in page)
        {
            var count = await campaigns.CountAsync(c => c.TemplateId == t.Id, ct);
            items.Add(new AdminTemplateDto(t.Id, t.Name, t.Slug, t.Category, t.Version, t.PackageUrl,
                t.Visibility, t.IsActive, t.AssignedEmail, count,
                TemplatePoster.OrNull(t.PreviewImageUrl), t.DesignerUserId is null ? null : t.DesignerName, t.CreatedAt, t.UpdatedAt,
                t.UnlistedByAdminAt));
        }
        return Paged(PagedResult<AdminTemplateDto>.Create(items, total, filter));
    }

    /// <summary>
    /// DELETE /api/admin/templates/{id} — removes a template. The rule is the one "My designs" uses —
    /// literally the same method (<see cref="IMyTemplatesService.DeleteAsync"/>), which lets an admin
    /// act on any template: a template any campaign already uses, or one made for a customer
    /// (Dedicated with an AssignedEmail — it's how they reach their invitation, even before a campaign
    /// exists), is DEACTIVATED (hidden from the gallery) instead of hard-deleted. Anything else is
    /// hard-deleted. This endpoint used to carry its own copy, which hard-deleted the made-for-someone
    /// case the other copy protected.
    /// </summary>
    [HttpDelete("{id:guid}")]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await removal.DeleteAsync(id, ct);
        return Success(new DeleteResultDto(result.Deleted, result.Unlisted, result.CampaignCount), result.Message);
    }
}
