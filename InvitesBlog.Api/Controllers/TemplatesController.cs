using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Filters.Templates;
using InvitesBlog.Application.Services.Templates;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>§10.1 Templates. Thin controller — delegates to <see cref="ITemplateService"/>.</summary>
[Route("api/templates")]
public sealed class TemplatesController(ITemplateService templates) : BaseApiController
{
    [HttpGet]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> List([FromQuery] TemplateFilter filter, CancellationToken ct) =>
        Paged(await templates.ListAsync(filter, ct));

    [HttpGet("{slug}")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> GetBySlug(string slug, CancellationToken ct) =>
        Success(await templates.GetBySlugAsync(slug, ct));

    [HttpGet("meta/categories")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> Categories(CancellationToken ct) =>
        Success(await templates.GetCategoriesAsync(ct));
}
