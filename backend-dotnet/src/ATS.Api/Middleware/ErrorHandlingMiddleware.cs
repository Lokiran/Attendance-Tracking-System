using Ats.Application.Common;
using Ats.Infrastructure.Database;

namespace Ats.Api.Middleware;

/// <summary>
/// Turns exceptions into the error envelope with the status codes of <c>backend/app/main.py</c>:
/// <see cref="AppException"/> → its status and code; unhandled constraint violation → 409 <c>conflict</c>;
/// database unreachable → 503 <c>database_unavailable</c>; anything else → 500 <c>internal_error</c>.
/// </summary>
internal sealed partial class ErrorHandlingMiddleware(RequestDelegate next, ILogger<ErrorHandlingMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // The client went away; nothing to answer.
        }
        catch (AppException ex)
        {
            await ErrorEnvelope.WriteAsync(context, ex.StatusCode, ex.Code, ex.Message, ex.Details,
                                           (ex as TooManyRequestsException)?.RetryAfterSeconds).ConfigureAwait(false);
        }
        catch (Exception ex) when (DatabaseErrors.Classify(ex) == DatabaseErrorKind.Conflict)
        {
            // Backstop: constraint violations not translated by a service become 409, never 500.
            LogIntegrity(logger, context.GetRequestId(), ex.GetType().Name);
            await ErrorEnvelope.WriteAsync(context, 409, "conflict", "The change conflicts with existing data.")
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (DatabaseErrors.Classify(ex) == DatabaseErrorKind.Unavailable)
        {
            LogUnavailable(logger, ex.GetType().Name);
            await ErrorEnvelope.WriteAsync(context, 503, "database_unavailable",
                                           "The database is temporarily unavailable. Please retry.").ConfigureAwait(false);
        }
#pragma warning disable CA1031 // the last-resort handler must catch everything
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogUnhandled(logger, ex, context.GetRequestId());
            await ErrorEnvelope.WriteAsync(context, 500, "internal_error", "An unexpected error occurred.")
                .ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "integrity error request_id={RequestId}: {ErrorType}")]
    private static partial void LogIntegrity(ILogger logger, string? requestId, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "database error: {ErrorType}")]
    private static partial void LogUnavailable(ILogger logger, string errorType);

    [LoggerMessage(Level = LogLevel.Error, Message = "unhandled error request_id={RequestId}")]
    private static partial void LogUnhandled(ILogger logger, Exception exception, string? requestId);
}
