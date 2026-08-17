using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// What remains of the separate designer sign-in: reading back the signed-in designer.
/// <para>
/// Registration, password sign-in and OAuth all moved to <c>/api/auth</c>, which issues ONE token
/// carrying every role the account holds. Keeping a parallel set here meant two places to get right
/// and a token that silently omitted the caller's other roles.
/// </para>
/// </summary>
[Route("api/designer/auth")]
public sealed class DesignerAuthController(IDesignerAuthService auth) : BaseApiController
{
    [HttpGet("me")]
    [HasPermission(Permissions.Designer.Manage)]
    public async Task<IActionResult> Me(CancellationToken ct) =>
        Success(await auth.MeAsync(ct));
}
