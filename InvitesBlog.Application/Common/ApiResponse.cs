namespace InvitesBlog.Application.Common;

/// <summary>A single field/error entry in a failed response.</summary>
public sealed record ApiError(string Message, string? Field = null, string? Code = null);

/// <summary>
/// The consistent API envelope returned by every endpoint (spec §API Structure — consistent
/// request/response formats via shared response helpers). The base controller wraps results in this.
/// </summary>
public sealed class ApiResponse<T>
{
    public bool Success { get; init; }
    public string? Message { get; init; }
    public T? Data { get; init; }
    public IReadOnlyList<ApiError>? Errors { get; init; }

    public static ApiResponse<T> Ok(T data, string? message = null) =>
        new() { Success = true, Data = data, Message = message };

    public static ApiResponse<T> Fail(string message, IReadOnlyList<ApiError>? errors = null) =>
        new() { Success = false, Message = message, Errors = errors };
}

/// <summary>Non-generic helper for messages with no payload.</summary>
public static class ApiResponse
{
    public static ApiResponse<object?> Message(string message) =>
        new() { Success = true, Message = message, Data = null };
}
