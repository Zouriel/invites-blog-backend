using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Inquiries;
using InvitesBlog.Application.Filters.Inquiries;
using InvitesBlog.Application.Services.Inquiries;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// Admin triage of custom-invitation inquiries: the queue (unattended first), a detail view, and saving
/// the consultation notes. The invitation itself is made and published for the customer in the designer.
/// </summary>
[Route("api/admin/inquiries")]
public sealed class AdminInquiriesController(IInquiryService inquiries) : BaseApiController
{
    [HttpGet]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> List([FromQuery] InquiryFilter filter, CancellationToken ct) =>
        Paged(await inquiries.ListAsync(filter, ct));

    [HttpGet("{id:guid}")]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Success(await inquiries.GetAsync(id, ct));

    [HttpPut("{id:guid}")]
    [HasPermission(Permissions.Templates.Manage)]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateInquiryRequest req, CancellationToken ct)
    {
        await inquiries.UpdateAsync(id, req, ct);
        return SuccessMessage("Inquiry updated.");
    }
}
