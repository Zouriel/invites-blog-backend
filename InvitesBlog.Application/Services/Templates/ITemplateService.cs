using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Templates;
using InvitesBlog.Application.Filters.Templates;

namespace InvitesBlog.Application.Services.Templates;

public interface ITemplateService
{
    Task<PagedResult<TemplateListItemDto>> ListAsync(TemplateFilter filter, CancellationToken ct = default);
    Task<TemplateDetailDto> GetBySlugAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetCategoriesAsync(CancellationToken ct = default);
}
