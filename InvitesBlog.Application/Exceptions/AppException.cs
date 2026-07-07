using System.Net;
using InvitesBlog.Application.Common;

namespace InvitesBlog.Application.Exceptions;

/// <summary>
/// Base application exception. Every domain failure derives from this so the global exception
/// middleware can translate it into a consistent <see cref="ApiResponse{T}"/> with the right
/// HTTP status (spec §Exception Handling — consistent formatting, no scattered messages).
/// </summary>
public abstract class AppException : Exception
{
    protected AppException(string message, HttpStatusCode statusCode, string errorCode,
        IReadOnlyList<ApiError>? errors = null) : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Errors = errors;
    }

    public HttpStatusCode StatusCode { get; }
    public string ErrorCode { get; }
    public IReadOnlyList<ApiError>? Errors { get; }
}

/// <summary>A requested resource does not exist (404).</summary>
public class NotFoundException(string message, string errorCode = "not_found")
    : AppException(message, HttpStatusCode.NotFound, errorCode);

/// <summary>A resource with the same identity already exists (409).</summary>
public class AlreadyExistsException(string message, string errorCode = "already_exists")
    : AppException(message, HttpStatusCode.Conflict, errorCode);

/// <summary>Request validation failed (422).</summary>
public class ValidationFailedException(string message, IReadOnlyList<ApiError> errors, string errorCode = "validation_failed")
    : AppException(message, HttpStatusCode.UnprocessableEntity, errorCode, errors);

/// <summary>Authentication is missing or invalid (401).</summary>
public class UnauthorizedException(string message = "Authentication is required.", string errorCode = "unauthorized")
    : AppException(message, HttpStatusCode.Unauthorized, errorCode);

/// <summary>Authenticated but not permitted (403).</summary>
public class ForbiddenException(string message = "You do not have permission to do this.", string errorCode = "forbidden")
    : AppException(message, HttpStatusCode.Forbidden, errorCode);

/// <summary>An operation is not valid for the resource's current state (409).</summary>
public class InvalidStateException(string message, string errorCode = "invalid_state")
    : AppException(message, HttpStatusCode.Conflict, errorCode);

/// <summary>A relationship/dependency prevents the operation (409).</summary>
public class DependencyConflictException(string message, string errorCode = "dependency_conflict")
    : AppException(message, HttpStatusCode.Conflict, errorCode);

/// <summary>A rule/business precondition failed (400).</summary>
public class BusinessRuleException(string message, string errorCode = "business_rule")
    : AppException(message, HttpStatusCode.BadRequest, errorCode);
