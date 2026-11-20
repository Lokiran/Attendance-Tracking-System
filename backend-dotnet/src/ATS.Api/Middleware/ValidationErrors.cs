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
        var details = new List<Detail>();
        foreach (var (key, entry) in context.ModelState)
        {
            foreach (var error in entry.Errors)
            {
                details.Add(new Detail(FieldPath(context, key), error.ErrorMessage.Length > 0
                    ? error.ErrorMessage
                    : error.Exception?.Message ?? "Invalid value"));
            }
        }

        var first = details.FirstOrDefault();
        var message = first is not null && first.Field.Length > 0 ? $"{first.Field}: {first.Message}" : "Invalid request";
        return new UnprocessableEntityObjectResult(new
        {
            error = new { code = "validation_error", message, details },
            request_id = context.HttpContext.GetRequestId(),
        });
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
