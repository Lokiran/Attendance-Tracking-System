using System.Globalization;

namespace Ats.Api.Middleware;

/// <summary>
/// Request id and security headers, as the <c>request_context</c> middleware in <c>backend/app/main.py</c>:
/// <list type="bullet">
/// <item>an incoming printable <c>X-Request-ID</c> is kept (at most 64 characters), otherwise a new
/// <c>uuid4().hex</c>; it is echoed in the response and in error bodies;</item>
/// <item><c>X-Content-Type-Options: nosniff</c>, <c>X-Frame-Options: DENY</c>, <c>Referrer-Policy: same-origin</c>;</item>
/// <item><c>Cache-Control: no-store</c> on <c>/api/*</c> except the docs pages.</item>
/// </list>
/// </summary>
internal sealed class RequestContextMiddleware(RequestDelegate next)
{
    public const string ItemKey = "ats.request_id";

    public Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers["X-Request-ID"].ToString();
        var id = incoming.Length > 0 && IsPrintable(incoming)
            ? incoming[..Math.Min(64, incoming.Length)]
            : Guid.NewGuid().ToString("N");
        context.Items[ItemKey] = id;
        context.TraceIdentifier = id;

        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers["X-Request-ID"] = id;
            headers.TryAdd("X-Content-Type-Options", "nosniff");
            headers.TryAdd("X-Frame-Options", "DENY");
            headers.TryAdd("Referrer-Policy", "same-origin");
            var path = context.Request.Path.Value ?? "";
            if (path.StartsWith("/api/", StringComparison.Ordinal)
                && !path.StartsWith("/api/docs", StringComparison.Ordinal)
                && !path.StartsWith("/api/redoc", StringComparison.Ordinal))
            {
                headers.TryAdd("Cache-Control", "no-store");
            }

            return Task.CompletedTask;
        });
        return next(context);
    }

    /// <summary>Python's <c>str.isprintable()</c>: no control, format, separator (except space) or unassigned chars.</summary>
    internal static bool IsPrintable(string text)
    {
        foreach (var c in text)
        {
            if (c == ' ')
            {
                continue;
            }

            switch (char.GetUnicodeCategory(c))
            {
                case UnicodeCategory.Control:
                case UnicodeCategory.Format:
                case UnicodeCategory.Surrogate:
                case UnicodeCategory.PrivateUse:
                case UnicodeCategory.OtherNotAssigned:
                case UnicodeCategory.LineSeparator:
                case UnicodeCategory.ParagraphSeparator:
                case UnicodeCategory.SpaceSeparator:
                    return false;
                default:
                    break;
            }
        }

        return true;
    }
}

internal static class RequestContextExtensions
{
    public static string? GetRequestId(this HttpContext context) =>
        context.Items.TryGetValue(RequestContextMiddleware.ItemKey, out var id) ? id as string : null;
}
