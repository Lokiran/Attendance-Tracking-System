namespace Ats.Application.Common;

/// <summary>
/// Application error rendered as the JSON error envelope
/// <c>{"error": {"code", "message", "details"?}, "request_id"}</c>.
/// Mirrors <c>app/core/errors.py</c>: same status codes and default codes.
/// </summary>
public class AppException : Exception
{
    public AppException(string message, string? code = null, int? statusCode = null, object? details = null)
        : base(message)
    {
        Code = code ?? DefaultCode;
        StatusCode = statusCode ?? DefaultStatus;
        Details = details;
    }

    public AppException()
        : this("Bad request")
    {
    }

    public AppException(string message, Exception innerException)
        : base(message, innerException)
    {
        Code = DefaultCode;
        StatusCode = DefaultStatus;
    }

    public string Code { get; }

    public int StatusCode { get; }

    public object? Details { get; }

    protected virtual string DefaultCode => "bad_request";

    protected virtual int DefaultStatus => 400;
}

/// <summary>422 <c>validation_error</c>.</summary>
public sealed class ValidationFailedException(string message, string? code = null, object? details = null)
    : AppException(message, code ?? "validation_error", 422, details);

/// <summary>401 <c>unauthorized</c> (or a more specific code such as <c>session_expired</c>).</summary>
public sealed class UnauthorizedException(string message, string? code = null)
    : AppException(message, code ?? "unauthorized", 401);

/// <summary>403 <c>forbidden</c> (or <c>csrf_failed</c>, <c>password_login_disabled</c>).</summary>
public sealed class ForbiddenException(string message, string? code = null)
    : AppException(message, code ?? "forbidden", 403);

/// <summary>404 <c>not_found</c>.</summary>
public sealed class NotFoundException(string message)
    : AppException(message, "not_found", 404);

/// <summary>409 <c>conflict</c>.</summary>
public sealed class ConflictException(string message)
    : AppException(message, "conflict", 409);

/// <summary>429 <c>too_many_requests</c>; the response carries <c>Retry-After</c>.</summary>
public sealed class TooManyRequestsException(string message, int retryAfterSeconds)
    : AppException(message, "too_many_requests", 429)
{
    public int RetryAfterSeconds { get; } = retryAfterSeconds;
}
