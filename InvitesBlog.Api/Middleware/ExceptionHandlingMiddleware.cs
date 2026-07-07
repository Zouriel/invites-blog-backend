using System.Net;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Exceptions;

namespace InvitesBlog.Api.Middleware;

/// <summary>
/// Global exception handler (spec §Exception Handling — consistent error formatting in one place).
/// Translates <see cref="AppException"/>s into their declared status + <see cref="ApiResponse{T}"/>,
/// and any unexpected error into a 500 without leaking internals.
/// </summary>
public sealed class ExceptionHandlingMiddleware(RequestDelegate next, ILogger<ExceptionHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (AppException ex)
        {
            logger.LogInformation("Handled {Exception}: {Message}", ex.GetType().Name, ex.Message);
            await WriteAsync(context, (int)ex.StatusCode,
                ApiResponse<object?>.Fail(ex.Message, ex.Errors));
        }
        catch (FluentValidation.ValidationException ex)
        {
            var errors = ex.Errors
                .Select(e => new ApiError(e.ErrorMessage, e.PropertyName, e.ErrorCode))
                .ToList();
            await WriteAsync(context, (int)HttpStatusCode.UnprocessableEntity,
                ApiResponse<object?>.Fail("Validation failed.", errors));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unhandled exception.");
            await WriteAsync(context, (int)HttpStatusCode.InternalServerError,
                ApiResponse<object?>.Fail("An unexpected error occurred."));
        }
    }

    private static Task WriteAsync(HttpContext context, int status, ApiResponse<object?> body)
    {
        if (context.Response.HasStarted) return Task.CompletedTask;
        context.Response.Clear();
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        return context.Response.WriteAsJsonAsync(body);
    }
}
