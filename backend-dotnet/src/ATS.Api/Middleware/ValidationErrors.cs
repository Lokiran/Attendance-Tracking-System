using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace Ats.Api.Middleware;

/// <summary>
/// Request validation failures as FastAPI reports them: 422 <c>validation_error</c> with
/// <c>details: [{field, message}]</c>, where <c>field</c> is Pydantic's location without <c>body</c>
/// (<c>query.page</c>, <c>path.employee_id</c>, <c>working_days.0</c>), and the message
/// <c>"&lt;field&gt;: &lt;message&gt;"</c> for the first problem.
/// </summary>
internal static partial class ValidationErrors
{
    public sealed record Detail(string Field, string Message);

    public static IActionResult Respond(ActionContext context)
    {
        var bodyParameters = context.ActionDescriptor.Parameters
            .Where(p => p.BindingInfo?.BindingSource == BindingSource.Body).Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var details = new List<Detail>();
        foreach (var (key, entry) in context.ModelState)
        {
            // MVC also reports "The body field is required." under the parameter name next to the real JSON error.
            if (bodyParameters.Contains(key) && context.ModelState.Count > 1)
            {
                continue;
            }

            foreach (var error in entry.Errors)
            {
                var text = error.ErrorMessage.Length > 0 ? error.ErrorMessage : error.Exception?.Message ?? "Invalid value";
                details.Add(new Detail(bodyParameters.Contains(key) ? "" : FieldPath(context, key), PydanticMessage(text)));
            }
        }

        return Envelope(context.HttpContext, details);
    }

    /// <summary>The 422 body: the first problem in the message, all of them in details.</summary>
    public static IActionResult Envelope(HttpContext http, IReadOnlyList<Detail> details)
    {
        ArgumentNullException.ThrowIfNull(details);
        var first = details.Count > 0 ? details[0] : null;
        var message = first is not null && first.Field.Length > 0 ? $"{first.Field}: {first.Message}" : "Invalid request";
        return new UnprocessableEntityObjectResult(new
        {
            error = new { code = "validation_error", message, details },
            request_id = http.GetRequestId(),
        });
    }

    /// <summary>Model-binding / JSON errors in Pydantic's words (decision D9: wording is best effort).</summary>
    internal static string PydanticMessage(string text)
    {
        if (text.Contains("non-empty request body is required", StringComparison.Ordinal) || text.EndsWith(" field is required.", StringComparison.Ordinal))
        {
            return "Field required";
        }

        if (text.Contains("could not be converted to", StringComparison.Ordinal))
        {
            return text switch
            {
                _ when text.Contains("System.String", StringComparison.Ordinal) => "Input should be a valid string",
                _ when text.Contains("System.Int32", StringComparison.Ordinal) || text.Contains("System.Int64", StringComparison.Ordinal) => "Input should be a valid integer",
                _ when text.Contains("System.Boolean", StringComparison.Ordinal) => "Input should be a valid boolean",
                _ when text.Contains("System.DateOnly", StringComparison.Ordinal) => "Input should be a valid date",
                _ when text.Contains("System.TimeOnly", StringComparison.Ordinal) => "Input should be a valid time",
                _ when text.Contains("List", StringComparison.Ordinal) => "Input should be a valid list",
                _ => "Input should be a valid value",
            };
        }

        if (text.Contains("is an invalid", StringComparison.Ordinal) || text.Contains("JSON", StringComparison.Ordinal))
        {
            return "JSON decode error";
        }

        return text;
    }

    /// <summary>ModelState key ("$.working_days[0]", "page", "WorkingDays") → Pydantic location string.</summary>
    internal static string FieldPath(ActionContext context, string key)
    {
        var parameter = context.ActionDescriptor.Parameters.FirstOrDefault(p =>
            string.Equals(p.Name, key, StringComparison.OrdinalIgnoreCase));
        var source = parameter?.BindingInfo?.BindingSource;
        if (source == BindingSource.Query)
        {
            return "query." + Snake(key);
        }

        if (source == BindingSource.Path)
        {
            return "path." + Snake(key);
        }

        var path = key.StartsWith("$.", StringComparison.Ordinal) ? key[2..] : key.TrimStart('$');
        path = Indexer().Replace(path, ".$1");
        return string.Join('.', path.Split('.', StringSplitOptions.RemoveEmptyEntries).Select(Snake));
    }

    private static string Snake(string name)
    {
        if (name.Contains('_', StringComparison.Ordinal) || name.All(c => !char.IsUpper(c)))
        {
            return name;
        }

        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]))
            {
                if (i > 0)
                {
                    sb.Append('_');
                }

                sb.Append(char.ToLowerInvariant(name[i]));
            }
            else
            {
                sb.Append(name[i]);
            }
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex Indexer();
}
