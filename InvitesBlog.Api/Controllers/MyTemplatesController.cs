using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// The templates the signed-in person published. Gated on <c>designer.manage</c>, which admins hold
/// too; everyone's list is their own (the full catalogue is on the admin screen).
/// </summary>
[Route("api/my-templates")]
[HasPermission(Permissions.Designer.Manage)]
public sealed class MyTemplatesController(IMyTemplatesService templates) : BaseApiController
{
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Success(await templates.ListAsync(ct));

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var result = await templates.DeleteAsync(id, ct);
        return Success(result, result.Message);
    }
}
