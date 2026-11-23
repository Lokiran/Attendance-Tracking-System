using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ats.Application.Validation;
using Ats.Domain;

namespace Ats.Api.Common;

/// <summary>A body field: whether the client sent it (Pydantic's <c>exclude_unset</c>) and its value (null when sent as null).</summary>
internal readonly record struct Sent<T>(bool Present, T? Value);

/// <summary>
/// Reads a JSON request body field by field as a Pydantic model does (lax mode for JSON input): "5" is accepted for an
/// int, 1 / "yes" for a bool; problems get Pydantic's messages and are added after the path and query problems of the
/// same <see cref="QueryReader"/>, the order FastAPI reports them in. Fields are read in the model's declaration order.
/// Unknown fields are ignored, as Pydantic ignores extra fields.
/// </summary>
internal sealed partial class BodyReader
{
    private readonly QueryReader _q;
    private readonly JsonElement _root;
    private readonly bool _isObject;

    public BodyReader(QueryReader q, JsonElement body)
    {
        _q = q;
        _root = body;
        _isObject = body.ValueKind == JsonValueKind.Object;
        if (!_isObject)
        {
            _q.BodyError("", "Input should be a valid dictionary or object to extract fields from");
        }
    }

    private bool TryGet(string name, out JsonElement value)
    {
        value = default;
        return _isObject && _root.TryGetProperty(name, out value);
    }

    private void Fail(string field, string message) => _q.BodyError(field, message);

    /// <summary>A string field. <paramref name="required"/>: missing is "Field required"; <paramref name="nullable"/>: null is allowed.</summary>
    public Sent<string> Str(string name, bool required = false, bool nullable = true, int? minLength = null, int? maxLength = null,
                            string? pattern = null)
    {
        if (!TryGet(name, out var v))
        {
            if (required && _isObject)
            {
                Fail(name, PyValidation.FieldRequired);
            }

            return default;
        }

        if (v.ValueKind == JsonValueKind.Null && nullable)
        {
            return new(true, null);
        }

        if (v.ValueKind != JsonValueKind.String)
        {
            Fail(name, "Input should be a valid string");
            return new(true, null);
        }

        var s = v.GetString()!;
        var length = s.EnumerateRunes().Count(); // Python's len(): code points
        if (minLength is { } min && length < min)
        {
            Fail(name, PyValidation.MinMessage(min));
        }
        else if (maxLength is { } max && length > max)
        {
            Fail(name, PyValidation.MaxMessage(max));
        }
        else if (pattern is not null && !Regex.IsMatch(s, pattern.EndsWith('$') ? pattern[..^1] + @"\z" : pattern)) // Rust regex: $ is the end
        {
            Fail(name, $"String should match pattern '{pattern}'");
        }
        else
        {
            return new(true, s);
        }

        return new(true, null);
    }

    /// <summary>Pydantic <c>EmailStr</c>.</summary>
    public Sent<string> Email(string name, bool required = false, bool nullable = true)
    {
        var s = Str(name, required, nullable);
        if (s.Value is { } text && PyValidation.EmailProblem(text) is { } problem)
        {
            Fail(name, $"value is not a valid email address: {problem}");
            return new(true, null);
        }

        return s;
    }

    public Sent<int?> Int(string name, bool required = false, bool nullable = true, int? ge = null, int? le = null)
    {
        if (!TryGet(name, out var v))
        {
            if (required && _isObject)
            {
                Fail(name, PyValidation.FieldRequired);
            }

            return default;
        }

        if (v.ValueKind == JsonValueKind.Null && nullable)
        {
            return new(true, null);
        }

        var value = IntValue(name, v);
        if (value is null)
        {
            return new(true, null);
        }

        if (ge is { } min && value < min)
        {
            Fail(name, $"Input should be greater than or equal to {min.ToString(CultureInfo.InvariantCulture)}");
            return new(true, null);
        }

        if (le is { } max && value > max)
        {
            Fail(name, $"Input should be less than or equal to {max.ToString(CultureInfo.InvariantCulture)}");
            return new(true, null);
        }

        return new(true, value);
    }

    private int? IntValue(string field, JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number when v.TryGetInt32(out var i):
                return i;
            case JsonValueKind.Number:
            {
                var d = v.GetDouble();
                if (d != Math.Floor(d))
                {
                    Fail(field, "Input should be a valid integer, got a number with a fractional part");
                    return null;
                }

                if (d is > int.MaxValue or < int.MinValue)
                {
                    Fail(field, "Input should be a valid integer");
                    return null;
                }

                return (int)d;
            }

            case JsonValueKind.String:
            {
                var t = v.GetString()!.Trim();
                if (int.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
                {
                    return n;
                }

                if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && double.IsFinite(d))
                {
                    if (d == Math.Floor(d) && d is <= int.MaxValue and >= int.MinValue)
                    {
                        return (int)d;
                    }

                    Fail(field, "Input should be a valid integer, got a number with a fractional part");
                    return null;
                }

                Fail(field, "Input should be a valid integer, unable to parse string as an integer");
                return null;
            }

            default:
                Fail(field, "Input should be a valid integer");
                return null;
        }
    }

    private static readonly string[] TrueWords = ["1", "on", "t", "true", "y", "yes"];
    private static readonly string[] FalseWords = ["0", "off", "f", "false", "n", "no"];

    public Sent<bool?> Bool(string name, bool nullable = true)
    {
        if (!TryGet(name, out var v))
        {
            return default;
        }

        switch (v.ValueKind)
        {
            case JsonValueKind.Null when nullable:
                return new(true, null);
            case JsonValueKind.True:
                return new(true, true);
            case JsonValueKind.False:
                return new(true, false);
            case JsonValueKind.Number when v.TryGetDouble(out var d) && d is 0 or 1:
                return new(true, d == 1);
            case JsonValueKind.Number:
                Fail(name, "Input should be a valid boolean, unable to interpret input");
                return new(true, null);
            case JsonValueKind.String:
            {
                var t = v.GetString()!.Trim().ToLowerInvariant();
                if (TrueWords.Contains(t) || FalseWords.Contains(t))
                {
                    return new(true, TrueWords.Contains(t));
                }

                Fail(name, "Input should be a valid boolean, unable to interpret input");
                return new(true, null);
            }

            default:
                Fail(name, "Input should be a valid boolean");
                return new(true, null);
        }
    }

    public Sent<DateOnly?> Date(string name, bool required = false, bool nullable = true)
    {
        if (!TryGet(name, out var v))
        {
            if (required && _isObject)
            {
                Fail(name, PyValidation.FieldRequired);
            }

            return default;
        }

        if (v.ValueKind == JsonValueKind.Null && nullable)
        {
            return new(true, null);
        }

        if (v.ValueKind != JsonValueKind.String)
        {
            Fail(name, "Input should be a valid date");
            return new(true, null);
        }

        var (date, problem) = QueryReader.PydanticDate(v.GetString()!);
        if (problem is not null)
        {
            Fail(name, problem);
        }

        return new(true, date);
    }

    public Sent<TimeOnly?> Time(string name, bool required = false, bool nullable = true)
    {
        if (!TryGet(name, out var v))
        {
            if (required && _isObject)
            {
                Fail(name, PyValidation.FieldRequired);
            }

            return default;
        }

        if (v.ValueKind == JsonValueKind.Null && nullable)
        {
            return new(true, null);
        }

        if (v.ValueKind != JsonValueKind.String)
        {
            Fail(name, "Input should be a valid time");
            return new(true, null);
        }

        var (time, problem) = PydanticTime(v.GetString()!);
        if (problem is not null)
        {
            Fail(name, problem);
        }

        return new(true, time);
    }

    /// <summary>Pydantic's time parsing (speedate): HH:MM[:SS[.ffffff]], with an optional Z / offset.</summary>
    internal static (TimeOnly? Time, string? Problem) PydanticTime(string s)
    {
        const string prefix = "Input should be in a valid time format, ";
        if (s.Length < 5)
        {
            return (null, prefix + "input is too short");
        }

        if (!char.IsAsciiDigit(s[0]) || !char.IsAsciiDigit(s[1]))
        {
            return (null, prefix + "invalid character in hour");
        }

        if (s[2] != ':')
        {
            return (null, prefix + "invalid time separator, expected `:`");
        }

        if (!char.IsAsciiDigit(s[3]) || !char.IsAsciiDigit(s[4]))
        {
            return (null, prefix + "invalid character in minute");
        }

        var m = TimeText().Match(s);
        if (!m.Success)
        {
            return (null, prefix + "unexpected extra characters at the end of the input");
        }

        var h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var mi = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        var sec = m.Groups["s"].Success ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
        if (h > 23)
        {
            return (null, prefix + "hour value is outside expected range of 0-23");
        }

        if (mi > 59)
        {
            return (null, prefix + "minute value is outside expected range of 0-59");
        }

        if (sec > 59)
        {
            return (null, prefix + "second value is outside expected range of 0-59");
        }

        var micro = m.Groups["f"].Success ? int.Parse(m.Groups["f"].Value.PadRight(6, '0'), CultureInfo.InvariantCulture) : 0;
        return (new TimeOnly(h, mi, sec).Add(TimeSpan.FromTicks(micro * 10L)), null);
    }

    public Sent<TEnum?> Enum<TEnum>(string name, bool nullable = true)
        where TEnum : struct, System.Enum
    {
        if (!TryGet(name, out var v))
        {
            return default;
        }

        if (v.ValueKind == JsonValueKind.Null && nullable)
        {
            return new(true, null);
        }

        if (v.ValueKind == JsonValueKind.String && EnumText.TryParse<TEnum>(v.GetString(), out var value))
        {
            return new(true, value);
        }

        var allowed = EnumText.All<TEnum>().ToArray();
        Fail(name, "Input should be " + string.Join(", ", allowed.Take(allowed.Length - 1).Select(a => $"'{a}'")) + $" or '{allowed[^1]}'");
        return new(true, null);
    }

    /// <summary>A list of ints (each item checked as an int) with Pydantic's length bounds.</summary>
    public Sent<List<int>> IntList(string name, bool required = false, bool nullable = true, int? minItems = null, int? maxItems = null)
    {
        if (!ListElement(name, required, nullable, out var v, out var sent))
        {
            return sent.Present ? new(true, null) : default;
        }

        var items = new List<int>();
        var ok = true;
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            var value = IntValue($"{name}.{i++}", item);
            ok &= value is not null;
            items.Add(value ?? 0);
        }

        return ok && CheckLength(name, items.Count, minItems, maxItems) ? new(true, items) : new(true, null);
    }

    public Sent<List<string>> StrList(string name, bool required = false, bool nullable = true, int? minItems = null, int? maxItems = null)
    {
        if (!ListElement(name, required, nullable, out var v, out var sent))
        {
            return sent.Present ? new(true, null) : default;
        }

        var items = new List<string>();
        var ok = true;
        var i = 0;
        foreach (var item in v.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                items.Add(item.GetString()!);
            }
            else
            {
                Fail($"{name}.{i}", "Input should be a valid string");
                ok = false;
            }

            i++;
        }

        return ok && CheckLength(name, items.Count, minItems, maxItems) ? new(true, items) : new(true, null);
    }

    private bool ListElement(string name, bool required, bool nullable, out JsonElement v, out Sent<object> sent)
    {
        sent = default;
        if (!TryGet(name, out v))
        {
            if (required && _isObject)
            {
                Fail(name, PyValidation.FieldRequired);
            }

            return false;
        }

        sent = new(true, null);
        if (v.ValueKind == JsonValueKind.Null && nullable)
        {
            return false;
        }

        if (v.ValueKind != JsonValueKind.Array)
        {
            Fail(name, "Input should be a valid list");
            return false;
        }

        return true;
    }

    private bool CheckLength(string name, int count, int? min, int? max)
    {
        if (min is { } lo && count < lo)
        {
            Fail(name, $"List should have at least {lo} item{(lo == 1 ? "" : "s")} after validation, not {count}");
            return false;
        }

        if (max is { } hi && count > hi)
        {
            Fail(name, $"List should have at most {hi} item{(hi == 1 ? "" : "s")} after validation, not {count}");
            return false;
        }

        return true;
    }

    /// <summary>A custom validator's problem ("Value error, ..." as Pydantic words a ValueError from a validator).</summary>
    public void ValueError(string name, string message) => Fail(name, "Value error, " + message);

    [GeneratedRegex(@"^(?<h>\d{2}):(?<m>\d{2})(?::(?<s>\d{2})(?:\.(?<f>\d{1,6}))?)?(?:Z|z|[+-]\d{2}:?\d{2})?$")]
    private static partial Regex TimeText();
}
