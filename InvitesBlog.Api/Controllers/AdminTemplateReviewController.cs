using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Filters.Designers;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// The community-template review queue. Gated by <c>designer.review</c> — deliberately a different
/// permission from the designer-side <c>designer.manage</c>, so a designer can never approve their own
/// submission.
/// </summary>
[Route("api/admin/template-submissions")]
[HasPermission(Permissions.Designer.Review)]
public sealed class AdminTemplateReviewController(ITemplateReviewService review) : BaseApiController
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] TemplateSubmissionFilter filter, CancellationToken ct) =>
        Paged(await review.ListAsync(filter, ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Success(await review.GetAsync(id, ct));

    [HttpPost("{id:guid}/review")]
    public async Task<IActionResult> Review(
        Guid id, [FromBody] ReviewSubmissionRequest request, CancellationToken ct) =>
        Success(await review.ReviewAsync(id, request, ct));
}
