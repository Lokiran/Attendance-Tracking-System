using System.Globalization;
using System.Text.RegularExpressions;
using Ats.Api.Middleware;
using Ats.Application.Common;
using Ats.Domain;

namespace Ats.Api.Common;

/// <summary>
/// Path and query parameters read the way FastAPI / Pydantic read them: the last value of a repeated parameter,
/// Pydantic's coercions (<c>"1.0"</c> is 1, <c>yes</c>/<c>on</c>/<c>1</c> are true, dates with a zero time are dates) and
/// its messages. Problems are collected in the order the caller reads the parameters (FastAPI's order: dependency
/// parameters such as the date range and paging first, then the endpoint's own path and query parameters) and
/// reported together as one 422.
/// </summary>
internal sealed partial class QueryReader(HttpRequest request)
{
    private static readonly string[] TrueValues = ["1", "on", "t", "true", "y", "yes"];
    private static readonly string[] FalseValues = ["0", "off", "f", "false", "n", "no"];

    private readonly List<ValidationErrors.Detail> _errors = [];

    public bool HasErrors => _errors.Count > 0;

    public int ErrorCount => _errors.Count;

    private string? Raw(string name) =>
        request.Query.TryGetValue(name, out var values) && values.Count > 0 ? values[^1] : null;

    private void Fail(string location, string name, string message) => _errors.Add(new ValidationErrors.Detail($"{location}.{name}", message));

    /// <summary>A problem with a request-body field (reported after the query and path problems, as FastAPI does).</summary>
    public void BodyError(string field, string message) => _errors.Add(new ValidationErrors.Detail(field, message));

    /// <summary>Throws the 422 with every problem found so far.</summary>
    public void ThrowIfInvalid()
    {
        if (_errors.Count > 0)
        {
            var first = _errors[0];
            throw new ValidationFailedException(first.Field.Length > 0 ? $"{first.Field}: {first.Message}" : "Invalid request", details: _errors.ToList());
        }
    }

    public int? Int(string name, int? ge = null, int? le = null) => ParseInt("query", name, Raw(name), ge, le);

    public int Int(string name, int fallback, int? ge = null, int? le = null) => ParseInt("query", name, Raw(name), ge, le) ?? fallback;

    /// <summary>A required int query parameter: "Field required" when absent.</summary>
    public int RequiredInt(string name, int? ge = null, int? le = null)
    {
        if (Raw(name) is null)
        {
            Fail("query", name, "Field required");
            return 0;
        }

        return ParseInt("query", name, Raw(name), ge, le) ?? 0;
    }

    public int PathInt(string name, string value) => ParseInt("path", name, value, null, null) ?? 0;

    private int? ParseInt(string location, string name, string? raw, int? ge, int? le)
    {
        if (raw is null)
        {
            return null;
        }

        int value;
        var text = raw.Trim();
        if (int.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
        {
            value = n;
        }
        else if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && !double.IsInfinity(d))
        {
            if (d != Math.Floor(d))
            {
                Fail(location, name, "Input should be a valid integer, got a number with a fractional part");
                return null;
            }

            if (d is > int.MaxValue or < int.MinValue)
            {
                Fail(location, name, "Input should be a valid integer, unable to parse string as an integer");
                return null;
            }

            value = (int)d;
        }
        else
        {
            Fail(location, name, "Input should be a valid integer, unable to parse string as an integer");
            return null;
        }

        if (ge is { } min && value < min)
        {
            Fail(location, name, $"Input should be greater than or equal to {min.ToString(CultureInfo.InvariantCulture)}");
            return null;
        }

        if (le is { } max && value > max)
        {
            Fail(location, name, $"Input should be less than or equal to {max.ToString(CultureInfo.InvariantCulture)}");
            return null;
        }

        return value;
    }

    /// <summary>An optional bool filter (<c>bool | None = None</c>): null when the parameter is absent or invalid.</summary>
    public bool? BoolOrNull(string name)
    {
        if (Raw(name) is null)
        {
            return null;
        }

        var before = _errors.Count;
        var value = Bool(name, false);
        return _errors.Count > before ? null : value;
    }

    public bool Bool(string name, bool fallback)
    {
        var raw = Raw(name);
        if (raw is null)
        {
            return fallback;
        }

        var lower = raw.Trim().ToLowerInvariant();
        if (TrueValues.Contains(lower))
        {
            return true;
        }

        if (FalseValues.Contains(lower))
        {
            return false;
        }

        Fail("query", name, "Input should be a valid boolean, unable to interpret input");
        return fallback;
    }

    public DateOnly? Date(string name) => ParseDate("query", name, Raw(name));

    public DateOnly PathDate(string name, string value) => ParseDate("path", name, value) ?? default;

    /// <summary>Pydantic's date parsing (speedate): YYYY-MM-DD, or a datetime whose time is exactly zero.</summary>
    private DateOnly? ParseDate(string location, string name, string? raw)
    {
        if (raw is null)
        {
            return null;
        }

        var (date, problem) = PydanticDate(raw);
        if (problem is not null)
        {
            Fail(location, name, problem);
        }

        return date;
    }

    internal static (DateOnly? Date, string? Problem) PydanticDate(string s)
    {
        const string prefix = "Input should be a valid date or datetime, ";
        if (s.Length < 10)
        {
            return (null, prefix + "input is too short");
        }

        static bool Digits(string t, int from, int count) => t.Substring(from, count).All(char.IsAsciiDigit);
        if (!Digits(s, 0, 4))
        {
            return (null, prefix + "invalid character in year");
        }

        if (s[4] != '-')
        {
            return (null, prefix + "invalid date separator, expected `-`");
        }

        if (!Digits(s, 5, 2))
        {
            return (null, prefix + "invalid character in month");
        }

        if (s[7] != '-')
        {
            return (null, prefix + "invalid date separator, expected `-`");
        }

        if (!Digits(s, 8, 2))
        {
            return (null, prefix + "invalid character in day");
        }

        var (y, m, d) = (int.Parse(s[..4], CultureInfo.InvariantCulture), int.Parse(s[5..7], CultureInfo.InvariantCulture),
                         int.Parse(s[8..10], CultureInfo.InvariantCulture));
        if (m is < 1 or > 12)
        {
            return (null, prefix + "month value is outside expected range of 1-12");
        }

        if (d < 1 || d > DateTime.DaysInMonth(Math.Max(1, y), m))
        {
            return (null, prefix + "day value is outside expected range");
        }

        var date = new DateOnly(Math.Max(1, y), m, d);
        if (s.Length == 10)
        {
            return (date, null);
        }

        if (s[10] is 'T' or 't' or ' ' or '_')
        {
            var time = TimePart().Match(s[11..]);
            if (!time.Success)
            {
                return (null, prefix + "invalid character in hour");
            }

            var zero = time.Groups["h"].Value == "00" && time.Groups["m"].Value == "00"
                       && (time.Groups["s"].Value is "" or "00") && time.Groups["f"].Value.All(c => c == '0');
            return zero ? (date, null) : (null, "Datetimes provided to dates should have zero time - e.g. be exact dates");
        }

        return (null, prefix + "unexpected extra characters at the end of the input");
    }

    public string? Str(string name, int? maxLength = null, string? pattern = null)
    {
        var raw = Raw(name);
        if (raw is null)
        {
            return null;
        }

        if (maxLength is { } max && raw.Length > max)
        {
            Fail("query", name, $"String should have at most {max.ToString(CultureInfo.InvariantCulture)} character{(max == 1 ? "" : "s")}");
            return null;
        }

        if (pattern is not null && !Regex.IsMatch(raw, pattern))
        {
            Fail("query", name, $"String should match pattern '{pattern}'");
            return null;
        }

        return raw;
    }

    /// <summary>A Literal parameter: "Input should be 'day', 'week' or 'month'".</summary>
    public string Choice(string name, string fallback, params string[] allowed)
    {
        var raw = Raw(name);
        if (raw is null)
        {
            return fallback;
        }

        if (allowed.Contains(raw))
        {
            return raw;
        }

        Fail("query", name, $"Input should be {Alternatives(allowed)}");
        return fallback;
    }

    public TEnum? Enum<TEnum>(string name)
        where TEnum : struct, Enum
    {
        var raw = Raw(name);
        if (raw is null)
        {
            return null;
        }

        if (EnumText.TryParse<TEnum>(raw, out var value))
        {
            return value;
        }

        Fail("query", name, $"Input should be {Alternatives([.. EnumText.All<TEnum>()])}");
        return null;
    }

    private static string Alternatives(string[] allowed) =>
        allowed.Length == 1
            ? $"'{allowed[0]}'"
            : string.Join(", ", allowed.Take(allowed.Length - 1).Select(a => $"'{a}'")) + $" or '{allowed[^1]}'";

    [GeneratedRegex(@"^(?<h>\d{2}):(?<m>\d{2})(?::(?<s>\d{2})(?:\.(?<f>\d{1,6}))?)?(?:Z|z|[+-]\d{2}:?\d{2})?$")]
    private static partial Regex TimePart();
}
