using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Features;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>Who can try unreleased features, and releasing them to everyone.</summary>
[Route("api/admin")]
[HasPermission(Permissions.Admin.ManageUsers)]
public sealed class AdminTestersController(ITesterAdminService testers) : BaseApiController
{
    [HttpGet("features")]
    public async Task<IActionResult> Features(CancellationToken ct) => Success(await testers.FeaturesAsync(ct));

    [HttpPut("features/{key}/release")]
    public async Task<IActionResult> Release(string key, [FromBody] ReleaseRequest request, CancellationToken ct) =>
        Success(await testers.SetReleasedAsync(key, request.Released, ct));

    [HttpGet("testers")]
    public async Task<IActionResult> List(CancellationToken ct) => Success(await testers.ListAsync(ct));

    [HttpPost("testers")]
    public async Task<IActionResult> Add([FromBody] SaveTesterRequest request, CancellationToken ct) =>
        Created(await testers.AddAsync(request, ct));

    [HttpPut("testers/{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] SaveTesterRequest request, CancellationToken ct) =>
        Success(await testers.UpdateAsync(id, request, ct));

    [HttpDelete("testers/{id:guid}")]
    public async Task<IActionResult> Remove(Guid id, CancellationToken ct)
    {
        await testers.RemoveAsync(id, ct);
        return SuccessMessage("Removed from testers.");
    }

    public sealed record ReleaseRequest(bool Released);
}

/// <summary>The features the signed-in account can use, so the app shows only what works for them.</summary>
[Route("api/me/features")]
public sealed class MyFeaturesController(IFeatureAccessService access) : BaseApiController
{
    [HttpGet]
    [HasPermission(Permissions.Templates.Read)]
    public async Task<IActionResult> Mine(CancellationToken ct) => Success(await access.MineAsync(ct));
}
