using InvitesBlog.Application.Common;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Middleware;

/// <summary>
/// What a request that fails model binding gets back: the same envelope, status and error shape as a
/// FluentValidation failure (<see cref="ExceptionHandlingMiddleware"/>).
///
/// <para>Without this, [ApiController] answers with ASP.NET's ValidationProblemDetails, whose
/// <c>errors</c> is an OBJECT of field → messages. The apps read <c>errors</c> as the envelope's ARRAY
/// of <c>{ message, field }</c>, so a missing required property produced a response their error
/// handling could not read. One API, one error shape.</para>
/// </summary>
public static class InvalidModelStateResponse
{
    public static IActionResult Create(ActionContext context)
    {
        var errors = context.ModelState
            .Where(entry => entry.Value is { Errors.Count: > 0 })
            .SelectMany(entry => entry.Value!.Errors.Select(error => new ApiError(
                // A binding exception (malformed JSON, say) carries no message of its own.
                string.IsNullOrWhiteSpace(error.ErrorMessage) ? "This value isn't valid." : error.ErrorMessage,
                FieldName(entry.Key))))
            .ToList();

        return new UnprocessableEntityObjectResult(ApiResponse<object?>.Fail("Validation failed.", errors));
    }

    /// <summary>
    /// The key as the caller would recognise it. System.Text.Json reports a body problem as a JSON path
    /// ("$.eventDate"), and an unreadable body as the whole document ("$"), which names no field.
    /// </summary>
    private static string? FieldName(string key)
    {
        var name = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key == "$" ? string.Empty : key;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }
}
