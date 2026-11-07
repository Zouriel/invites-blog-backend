using System.Text.Json;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Exceptions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// An answer that may take minutes (a design assistant step: Claude thinking and writing tool calls).
/// Cloudflare drops a request whose origin sends nothing for 100 seconds, so once the work has taken a
/// few seconds this starts the 200 response and writes a space every few seconds (leading whitespace is
/// valid JSON), then the usual envelope.
///
/// <para>Refusals thrown early (not signed in, not Premium, bad input) still reach the exception
/// middleware with their own status. A failure after the response has started can only be told in the
/// body: <c>{ success: false, message }</c>, which the app reads like any refusal.</para>
/// </summary>
public sealed class KeepAliveResult<T>(Task<T> work) : IActionResult
{
    public static TimeSpan Grace { get; set; } = TimeSpan.FromSeconds(4);
    public static TimeSpan Beat { get; set; } = TimeSpan.FromSeconds(10);

    public async Task ExecuteResultAsync(ActionContext context)
    {
        var http = context.HttpContext;
        var json = http.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;

        // Quick answers (and early refusals) go out the ordinary way.
        if (await Task.WhenAny(work, Task.Delay(Grace, http.RequestAborted)) == work)
        {
            await new OkObjectResult(ApiResponse<T>.Ok(await work)).ExecuteResultAsync(context);
            return;
        }

        var response = http.Response;
        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers.CacheControl = "no-store";
        // Tell proxies not to hold the spaces back.
        response.Headers["X-Accel-Buffering"] = "no";
        await response.StartAsync(http.RequestAborted);

        while (await Task.WhenAny(work, Task.Delay(Beat, http.RequestAborted)) != work)
        {
            await response.WriteAsync(" ", http.RequestAborted);
            await response.Body.FlushAsync(http.RequestAborted);
        }

        object envelope;
        try
        {
            envelope = ApiResponse<T>.Ok(await work);
        }
        catch (AppException e)
        {
            envelope = ApiResponse<T>.Fail(e.Message);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            http.RequestServices.GetRequiredService<ILogger<KeepAliveResult<T>>>().LogError(e, "A long request failed after its response had started.");
            envelope = ApiResponse<T>.Fail("Something went wrong. Please try again.");
        }
        await JsonSerializer.SerializeAsync(response.Body, envelope, envelope.GetType(), json, http.RequestAborted);
    }
}
