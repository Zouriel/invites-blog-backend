using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// The templates the signed-in person manages. Gated on <c>designer.manage</c>, which admins hold
/// too — the service then scopes the list by role: everything for an admin, their own for a designer.
/// </summary>
[Route("api/my-templates")]
[HasPermission(Permissions.Designer.Manage)]
public sealed class MyTemplatesController(IMyTemplatesService templates) : BaseApiController
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Success(await templates.ListAsync(ct));

    [HttpGet("{id:guid}/source")]
    public async Task<IActionResult> Source(Guid id, CancellationToken ct) =>
        Success(await templates.GetSourceAsync(id, ct));

    [HttpPut("{id:guid}/pricing")]
    public async Task<IActionResult> SetPricing(
        Guid id, [FromBody] SetTemplatePricingRequest request, CancellationToken ct) =>
        Success(await templates.SetPricingAsync(id, request, ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await templates.DeleteAsync(id, ct);
        return Success(result, result.Message);
    }
}
