using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace Ats.Api.Middleware;

/// <summary>
/// Writes the error body every failure uses (<c>_error</c> in <c>backend/app/main.py</c>):
/// <c>{"error": {"code", "message", "details"?}, "request_id"}</c>.
/// </summary>
internal static class ErrorEnvelope
{
    public static async Task WriteAsync(HttpContext context, int status, string code, string message,
                                        object? details = null, int? retryAfterSeconds = null)
    {
        var response = context.Response;
        if (response.HasStarted)
        {
            return; // too late to change anything; the connection is aborted by the server
        }

        response.StatusCode = status;
        response.ContentType = "application/json";
        if (retryAfterSeconds is { } seconds)
        {
            response.Headers.RetryAfter = seconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        var options = context.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.SerializerOptions;
        using var buffer = new MemoryStream();
        await using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("error");
            writer.WriteString("code", code);
            writer.WriteString("message", message);
            if (details is not null)
            {
                writer.WritePropertyName("details");
                JsonSerializer.Serialize(writer, details, details.GetType(), options);
            }

            writer.WriteEndObject();
            writer.WritePropertyName("request_id");
            if (context.GetRequestId() is { } id)
            {
                writer.WriteStringValue(id);
            }
            else
            {
                writer.WriteNullValue();
            }

            writer.WriteEndObject();
        }

        response.ContentLength = buffer.Length;
        buffer.Position = 0;
        await buffer.CopyToAsync(response.Body, context.RequestAborted).ConfigureAwait(false);
    }
}
