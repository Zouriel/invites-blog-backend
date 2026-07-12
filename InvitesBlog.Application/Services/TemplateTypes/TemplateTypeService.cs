using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.TemplateTypes;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Filters.TemplateTypes;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.TemplateTypes;

public interface ITemplateTypeService
{
    Task<IReadOnlyList<TemplateTypeDto>> ListAsync(bool includeInactive, CancellationToken ct = default);
    Task<PagedResult<TemplateTypeDto>> ListPagedAsync(TemplateTypeFilter filter, CancellationToken ct = default);
    Task<TemplateTypeDto> CreateAsync(CreateTemplateTypeRequest req, CancellationToken ct = default);
    Task DeactivateAsync(Guid id, CancellationToken ct = default);
}

/// <summary>Manages template types (§4.5.1) — the admin-editable list of categories.</summary>
public sealed class TemplateTypeService(IRepository<TemplateType> types, IUnitOfWork uow) : ITemplateTypeService
{
    public async Task<IReadOnlyList<TemplateTypeDto>> ListAsync(bool includeInactive, CancellationToken ct = default)
    {
        var query = types.Query();
        if (!includeInactive) query = query.Where(t => t.IsActive);
        return await query.OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Select(t => new TemplateTypeDto(t.Id, t.Name, t.Slug, t.SortOrder, t.IsActive))
            .ToListAsync(ct);
    }

    public async Task<PagedResult<TemplateTypeDto>> ListPagedAsync(TemplateTypeFilter filter, CancellationToken ct = default)
    {
        var query = types.Query(); // admin view: includes inactive
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            query = query.Where(t => t.Name.ToLower().Contains(term) || t.Slug.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(t => t.SortOrder).ThenBy(t => t.Name)
            .Skip(filter.Skip).Take(filter.PageSize)
            .Select(t => new TemplateTypeDto(t.Id, t.Name, t.Slug, t.SortOrder, t.IsActive))
            .ToListAsync(ct);

        return PagedResult<TemplateTypeDto>.Create(items, total, filter);
    }

    public async Task<TemplateTypeDto> CreateAsync(CreateTemplateTypeRequest req, CancellationToken ct = default)
    {
        var name = req.Name?.Trim() ?? "";
        if (name.Length == 0) throw new BusinessRuleException("A name is required.", "template_type_name_required");
        var slug = Slugify(name);
        if (await types.AnyAsync(t => t.Slug == slug, ct))
            throw new AlreadyExistsException($"A template type '{name}' already exists.", "template_type_exists");

        var max = await types.Query().Select(t => (int?)t.SortOrder).MaxAsync(ct) ?? 0;
        var entity = new TemplateType
        {
            Id = Guid.NewGuid(), Name = name, Slug = slug,
            SortOrder = max + 10, IsActive = true, CreatedAt = DateTimeOffset.UtcNow
        };
        await types.AddAsync(entity, ct);
        await uow.SaveChangesAsync(ct);
        return new TemplateTypeDto(entity.Id, entity.Name, entity.Slug, entity.SortOrder, entity.IsActive);
    }

    public async Task DeactivateAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await types.GetByIdAsync(id, ct)
            ?? throw new NotFoundException($"Template type '{id}' was not found.", "template_type_not_found");
        entity.IsActive = false;
        types.Update(entity);
        await uow.SaveChangesAsync(ct);
    }

    private static string Slugify(string input)
    {
        var slug = new string(input.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        return slug.Trim('-');
    }
}
