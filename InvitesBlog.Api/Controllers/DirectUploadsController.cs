using InvitesBlog.Application.Services.Photos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

public sealed record DirectUploadStatusRequest(IReadOnlyList<Guid>? Ids);

/// <summary>
/// The second half of a direct upload. Open to anyone holding the upload's id: the door was checked
/// when the link was issued, the id is random and goes only to whoever asked for it, and all it can do
/// is move on a file that is already waiting its turn. Every kind of uploader ends up here, including a
/// QR contributor, who has no account to authorize with.
/// </summary>
[ApiController]
[Route("api/uploads")]
[AllowAnonymous]
public sealed class DirectUploadsController(IDirectUploadService uploads) : BaseApiController
{
    [HttpPost("{id:guid}/complete")]
    [EnableRateLimiting("upload-progress")]
    public async Task<IActionResult> Complete(Guid id, CancellationToken ct) =>
        Success(await uploads.CompleteAsync(id, ct));

    [HttpPost("status")]
    [EnableRateLimiting("upload-progress")]
    public async Task<IActionResult> Status([FromBody] DirectUploadStatusRequest req, CancellationToken ct) =>
        Success(await uploads.StatusAsync(req.Ids ?? [], ct));
}
