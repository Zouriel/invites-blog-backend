using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// Releasing a commissioned template to the public gallery. Both parties act here, each on their own
/// half: the designer with their designer session, the requester with the OTP-verified email the
/// template was commissioned for — the same identity they already use to claim it.
/// </summary>
[Route("api/template-release")]
public sealed class TemplateReleaseController(ITemplateReleaseService release) : BaseApiController
{
    [HttpGet("{templateId:guid}")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> Get(Guid templateId, CancellationToken ct) =>
        Success(await release.GetAsync(templateId, ct));

    [HttpPost("{templateId:guid}/designer-consent")]
    [HasPermission(Permissions.Designer.Manage)]
    public async Task<IActionResult> DesignerConsent(Guid templateId, CancellationToken ct) =>
        Success(await release.ConsentAsDesignerAsync(templateId, ct));

    [HttpPost("{templateId:guid}/requester-consent")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> RequesterConsent(Guid templateId, CancellationToken ct) =>
        Success(await release.ConsentAsRequesterAsync(templateId, ct));

    /// <summary>The signed-in requester's commissioned templates, for the dashboard's release card.</summary>
    [HttpGet("/api/me/commissioned-templates")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> Mine(CancellationToken ct) =>
        Success(await release.ListForRequesterAsync(ct));
}
