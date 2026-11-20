using System.Globalization;
using System.Text;

namespace Ats.Application.Python;

/// <summary>Text the Python backend produces with <c>isoformat()</c> and <c>repr()</c>, where it is stored or shown.</summary>
public static class PyFormat
{
    /// <summary><c>datetime.isoformat()</c> of an aware UTC value: <c>2026-11-19T08:39:37.577436+00:00</c>.</summary>
    public static string IsoDateTime(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var micro = utc.Ticks % TimeSpan.TicksPerSecond / 10;
        var text = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return (micro == 0 ? text : $"{text}.{micro.ToString("D6", CultureInfo.InvariantCulture)}") + "+00:00";
    }

    /// <summary><c>date.isoformat()</c>.</summary>
    public static string IsoDate(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary><c>time.isoformat()</c>.</summary>
    public static string IsoTime(TimeOnly value) => Json.PythonTimeConverter.Format(value);

    /// <summary>
    /// <c>repr(str)</c>: single quotes unless the text contains a single quote and no double quote; backslash,
    /// the quote, and control characters escaped (<c>'10001'</c>, <c>"O'Brien"</c>).
    /// </summary>
    public static string Repr(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var quote = text.Contains('\'', StringComparison.Ordinal) && !text.Contains('"', StringComparison.Ordinal) ? '"' : '\'';
        var sb = new StringBuilder(text.Length + 2).Append(quote);
        foreach (var c in text)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c == quote)
                    {
                        sb.Append('\\').Append(c);
                    }
                    else if (c < 0x20 || c == 0x7f)
                    {
                        sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append(quote).ToString();
    }
}
