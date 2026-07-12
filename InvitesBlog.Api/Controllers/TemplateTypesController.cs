using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.TemplateTypes;
using InvitesBlog.Application.Filters.TemplateTypes;
using InvitesBlog.Application.Services.TemplateTypes;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>Template types (§4.5.1) — public list for pickers; admin create/deactivate.</summary>
[Route("api")]
public sealed class TemplateTypesController(ITemplateTypeService service) : BaseApiController
{
    /// <summary>Public list of active template types (gallery filter, wizard, upload picker).</summary>
    [HttpGet("template-types")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Success(await service.ListAsync(includeInactive: false, ct));

    /// <summary>Admin list — paged + searchable, includes inactive types.</summary>
    [HttpGet("admin/template-types")]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> ListAll([FromQuery] TemplateTypeFilter filter, CancellationToken ct) =>
        Paged(await service.ListPagedAsync(filter, ct));

    [HttpPost("admin/template-types")]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> Create([FromBody] CreateTemplateTypeRequest req, CancellationToken ct) =>
        Created(await service.CreateAsync(req, ct));

    [HttpDelete("admin/template-types/{id:guid}")]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> Deactivate(Guid id, CancellationToken ct)
    {
        await service.DeactivateAsync(id, ct);
        return SuccessMessage("Template type deactivated.");
    }
}
