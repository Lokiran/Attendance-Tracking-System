using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Ats.Application.Python;

/// <summary>
/// Python's <c>json.dumps(value)</c> with default arguments, which SQLAlchemy uses for JSON columns:
/// separators <c>", "</c> and <c>": "</c>, <c>ensure_ascii=True</c> (<c>–</c>, lower-case hex), floats as
/// <c>repr()</c>. Writing JSON columns this way keeps rows written by .NET identical to rows written by Python.
/// </summary>
public static class PyJson
{
    public static string Dumps(JsonNode? node)
    {
        var sb = new StringBuilder();
        Write(sb, node);
        return sb.ToString();
    }

    public static string Dumps(IEnumerable<int> values) =>
        "[" + string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture))) + "]";

    public static string Dumps(IEnumerable<string> values)
    {
        var sb = new StringBuilder("[");
        var first = true;
        foreach (var v in values)
        {
            if (!first)
            {
                sb.Append(", ");
            }

            WriteString(sb, v);
            first = false;
        }

        return sb.Append(']').ToString();
    }

    private static void Write(StringBuilder sb, JsonNode? node)
    {
        switch (node)
        {
            case null:
                sb.Append("null");
                break;
            case JsonObject obj:
                sb.Append('{');
                var first = true;
                foreach (var (key, value) in obj)
                {
                    if (!first)
                    {
                        sb.Append(", ");
                    }

                    WriteString(sb, key);
                    sb.Append(": ");
                    Write(sb, value);
                    first = false;
                }

                sb.Append('}');
                break;
            case JsonArray array:
                sb.Append('[');
                for (var i = 0; i < array.Count; i++)
                {
                    if (i > 0)
                    {
                        sb.Append(", ");
                    }

                    Write(sb, array[i]);
                }

                sb.Append(']');
                break;
            case JsonValue value:
                WriteValue(sb, value);
                break;
            default:
                throw new NotSupportedException(node.GetType().Name);
        }
    }

    private static void WriteValue(StringBuilder sb, JsonValue value)
    {
        if (value.TryGetValue<JsonElement>(out var element))
        {
            switch (element.ValueKind)
            {
                case JsonValueKind.String:
                    WriteString(sb, element.GetString()!);
                    return;
                case JsonValueKind.Number:
                    // Parsed from stored JSON (written by Python): keep the number exactly as written.
                    sb.Append(element.GetRawText());
                    return;
                case JsonValueKind.True:
                    sb.Append("true");
                    return;
                case JsonValueKind.False:
                    sb.Append("false");
                    return;
                case JsonValueKind.Null:
                    sb.Append("null");
                    return;
                default:
                    Write(sb, JsonNode.Parse(element.GetRawText()));
                    return;
            }
        }

        if (value.TryGetValue<string>(out var s))
        {
            WriteString(sb, s);
        }
        else if (value.TryGetValue<bool>(out var b))
        {
            sb.Append(b ? "true" : "false");
        }
        else if (value.TryGetValue<double>(out var d) && !value.TryGetValue<long>(out _))
        {
            sb.Append(FloatRepr(d));
        }
        else if (value.TryGetValue<decimal>(out var m) && m != decimal.Truncate(m))
        {
            sb.Append(FloatRepr((double)m));
        }
        else
        {
            // Integers (and anything else numeric): the invariant text System.Text.Json produces.
            sb.Append(value.ToJsonString());
        }
    }

    /// <summary>A JSON string with Python's escaping (ensure_ascii=True).</summary>
    public static void WriteString(StringBuilder sb, string text)
    {
        ArgumentNullException.ThrowIfNull(sb);
        ArgumentNullException.ThrowIfNull(text);
        sb.Append('"');
        foreach (var c in text)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                default:
                    if (c < 0x20 || c > 0x7e)
                    {
                        sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
    }

    /// <summary>
    /// Python's <c>repr(float)</c>: the shortest round-trip digits, fixed notation for 1e-4 &lt;= |x| &lt; 1e16
    /// (with a trailing <c>.0</c> when integral), otherwise <c>1e+16</c> / <c>1.5e-05</c>.
    /// </summary>
    public static string FloatRepr(double value)
    {
        if (double.IsNaN(value))
        {
            return "NaN";
        }

        if (double.IsInfinity(value))
        {
            return value > 0 ? "Infinity" : "-Infinity";
        }

        if (value == 0)
        {
            return double.IsNegative(value) ? "-0.0" : "0.0";
        }

        // Shortest round-trip digits ("R") split into a digit string and the decimal point position.
        var shortest = value.ToString("R", CultureInfo.InvariantCulture);
        var negative = value < 0;
        var digits = new StringBuilder();
        int exponent;
        var eIndex = shortest.IndexOfAny(['E', 'e']);
        var mantissa = eIndex >= 0 ? shortest[..eIndex] : shortest;
        var exp10 = eIndex >= 0 ? int.Parse(shortest[(eIndex + 1)..], CultureInfo.InvariantCulture) : 0;
        mantissa = mantissa.TrimStart('-');
        var dot = mantissa.IndexOf('.', StringComparison.Ordinal);
        var intPart = dot >= 0 ? mantissa[..dot] : mantissa;
        var fracPart = dot >= 0 ? mantissa[(dot + 1)..] : "";
        digits.Append(intPart).Append(fracPart);
        exponent = intPart.Length + exp10;              // position of the decimal point within digits
        var all = digits.ToString().TrimStart('0');
        exponent -= digits.Length - digits.ToString().TrimStart('0').Length;
        all = all.TrimEnd('0');
        if (all.Length == 0)
        {
            return negative ? "-0.0" : "0.0";
        }

        var sci = exponent - 1;                          // exponent in d.ddd × 10^sci
        string text;
        if (sci < -4 || sci >= 16)
        {
            var m = all.Length > 1 ? $"{all[0]}.{all[1..]}" : all;
            text = $"{m}e{(sci < 0 ? "-" : "+")}{Math.Abs(sci).ToString("D2", CultureInfo.InvariantCulture)}";
        }
        else if (exponent <= 0)
        {
            text = "0." + new string('0', -exponent) + all;
        }
        else if (exponent >= all.Length)
        {
            text = all + new string('0', exponent - all.Length) + ".0";
        }
        else
        {
            text = $"{all[..exponent]}.{all[exponent..]}";
        }

        return negative ? "-" + text : text;
    }
}
