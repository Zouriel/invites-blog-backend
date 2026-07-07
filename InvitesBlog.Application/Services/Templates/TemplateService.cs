using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Templates;
using InvitesBlog.Application.Exceptions.Templates;
using InvitesBlog.Application.Filters.Templates;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Templates;

/// <summary>Template gallery business logic (spec §Services). Coordinates the repository + filter.</summary>
public sealed class TemplateService(ITemplateRepository templates) : ITemplateService
{
    public async Task<PagedResult<TemplateListItemDto>> ListAsync(TemplateFilter filter, CancellationToken ct = default)
    {
        var query = templates.Query().Where(t => t.IsActive);

        if (!string.IsNullOrWhiteSpace(filter.Category))
            query = query.Where(t => t.Category == filter.Category);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(t => t.Name.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(t => t.Name)
            .Skip(filter.Skip).Take(filter.PageSize)
            .Select(t => ToListItem(t))
            .ToListAsync(ct);

        return PagedResult<TemplateListItemDto>.Create(items, total, filter);
    }

    public async Task<TemplateDetailDto> GetBySlugAsync(string slug, CancellationToken ct = default)
    {
        var t = await templates.GetActiveBySlugAsync(slug, ct)
                ?? throw new TemplateNotFoundException(slug);
        return new TemplateDetailDto(t.Id, t.Name, t.Slug, t.Category, t.Description, t.Version,
            t.PreviewImageUrl, t.PreviewAnimationUrl, t.IsPremium, t.DesignerName, t.PackageUrl, t.ManifestJson);
    }

    public async Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default) =>
        await templates.Query().Where(t => t.IsActive)
            .Select(t => t.Category).Distinct().OrderBy(c => c).ToListAsync(ct);

    private static TemplateListItemDto ToListItem(Template t) => new(
        t.Id, t.Name, t.Slug, t.Category, t.Description,
        t.PreviewImageUrl, t.PreviewAnimationUrl, t.IsPremium, t.DesignerName, t.PackageUrl, t.Version);
}
