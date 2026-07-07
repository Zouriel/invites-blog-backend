using InvitesBlog.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// Shared controller behavior (spec §Base Controller): consistent success/created/paged responses
/// via the <see cref="ApiResponse{T}"/> envelope so no controller repeats response-shaping logic.
/// Controllers stay thin — they only translate HTTP to service calls.
/// </summary>
[ApiController]
[Route("api/[controller]")]
[Produces("application/json")]
public abstract class BaseApiController : ControllerBase
{
    protected IActionResult Success<T>(T data, string? message = null) =>
        Ok(ApiResponse<T>.Ok(data, message));

    protected IActionResult SuccessMessage(string message) =>
        Ok(ApiResponse.Message(message));

    protected IActionResult Created<T>(T data, string? message = null) =>
        StatusCode(StatusCodes.Status201Created, ApiResponse<T>.Ok(data, message));

    protected IActionResult Paged<T>(PagedResult<T> page) =>
        Ok(ApiResponse<PagedResult<T>>.Ok(page));
}
