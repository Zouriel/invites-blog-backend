using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Filters.Templates;
using InvitesBlog.Application.Services.Templates;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>§10.1 Templates. Thin controller — delegates to <see cref="ITemplateService"/>.</summary>
[Route("api/templates")]
public sealed class TemplatesController(ITemplateService templates, ICurrentUser currentUser) : BaseApiController
{
    /// <summary>The "My requests" tab — active dedicated templates reserved for the caller's verified
    /// email. Either identity carries that email: an account session or an OTP-JWT. Empty list ⇒ the
    /// frontend shows "nothing reserved for you yet".</summary>
    [HttpGet("/api/me/dedicated-templates")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> MyDedicated(CancellationToken ct) =>
        Success(await templates.GetDedicatedForAsync(currentUser.Contact ?? "", ct));

    /// <summary>Templates the caller published themselves — private ones included, so the event
    /// picker can offer somebody their own design without making them publish it to the world.</summary>
    [HttpGet("/api/me/templates")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> Mine(CancellationToken ct) =>
        Success(await templates.GetMineAsync(ct));

    [HttpGet]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> List([FromQuery] TemplateFilter filter, CancellationToken ct) =>
        Paged(await templates.ListAsync(filter, ct));

    [HttpGet("{slug}")]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> GetBySlug(string slug, CancellationToken ct) =>
        Success(await templates.GetBySlugAsync(slug, ct));
}
