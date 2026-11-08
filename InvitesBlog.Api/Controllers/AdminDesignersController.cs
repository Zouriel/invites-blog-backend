using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Filters.Designers;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>Designer management: the list, and suspending or reinstating an account.</summary>
[Route("api/admin/designers")]
[HasPermission(Permissions.Designer.Review)]
public sealed class AdminDesignersController(IDesignerAdminService designers) : BaseApiController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] DesignerFilter filter, CancellationToken ct) =>
        Paged(await designers.ListAsync(filter, ct));

    /// <summary>
    /// Suspend or reinstate. Suspending blocks sign-ins but deliberately leaves
    /// their published templates live, so no inviter's campaign breaks.
    /// </summary>
    [HttpPost("{id:guid}/suspend")]
    public async Task<IActionResult> Suspend(Guid id, [FromQuery] bool suspended, CancellationToken ct) =>
        Success(await designers.SetSuspendedAsync(id, suspended, ct));

    /// <summary>Turns AI pictures on or off for the account. Each picture costs money, so it's off by default.</summary>
    [HttpPost("{id:guid}/ai-pictures")]
    public async Task<IActionResult> AiPictures(Guid id, [FromQuery] bool allowed, CancellationToken ct) =>
        Success(await designers.SetAiPicturesAsync(id, allowed, ct));
}
