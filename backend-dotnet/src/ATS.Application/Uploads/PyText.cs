using System.Globalization;
using System.Numerics;
using System.Text;
using Ats.Application.Python;

namespace Ats.Application.Uploads;

/// <summary>
/// Python's text behaviour where an import depends on it: <c>str()</c> of a cell value, <c>strip()</c> /
/// <c>split()</c> (Python whitespace includes U+001C..U+001F), and slicing by code points.
/// </summary>
public static class PyText
{
    public static bool IsSpace(char c) => char.IsWhiteSpace(c) || c is >= '\x1c' and <= '\x1f';

    /// <summary><c>str.strip()</c>.</summary>
    public static string Strip(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int start = 0, end = text.Length;
        while (start < end && IsSpace(text[start]))
        {
            start++;
        }

        while (end > start && IsSpace(text[end - 1]))
        {
            end--;
        }

        return text[start..end];
    }

    /// <summary><c>" ".join(text.split())</c>: runs of whitespace become one space, none at the ends.</summary>
    public static string SplitJoin(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sb = new StringBuilder(text.Length);
        var pending = false;
        foreach (var c in text)
        {
            if (IsSpace(c))
            {
                pending = sb.Length > 0;
                continue;
            }

            if (pending)
            {
                sb.Append(' ');
                pending = false;
            }

            sb.Append(c);
        }

        return sb.ToString();
    }

    /// <summary><c>text[:n]</c> (code points, so a surrogate pair is never cut).</summary>
    public static string Slice(string text, int n)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length <= n)
        {
            return text;
        }

        var i = 0;
        var count = 0;
        while (i < text.Length && count < n)
        {
            i += char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]) ? 2 : 1;
            count++;
        }

        return text[..i];
    }

    /// <summary><c>timesheet_files.cell_text</c>: "" for nothing, 45000.0 as "45000", else <c>str(v).strip()</c>.</summary>
    public static string CellText(object? value) => value switch
    {
        null => "",
        double d when double.IsFinite(d) && Math.Floor(d) == d => new BigInteger(d).ToString(CultureInfo.InvariantCulture),
        string s => Strip(s),
        _ => Strip(Str(value)),
    };

    /// <summary><c>str(v)</c> of the values a reader produces.</summary>
    public static string Str(object? value) => value switch
    {
        null => "None",
        string s => s,
        bool b => b ? "True" : "False",
        double d => double.IsNaN(d) ? "nan" : double.IsPositiveInfinity(d) ? "inf" : double.IsNegativeInfinity(d) ? "-inf" : PyJson.FloatRepr(d),
        int i => i.ToString(CultureInfo.InvariantCulture),
        long l => l.ToString(CultureInfo.InvariantCulture),
        DateTime t => DateTimeStr(t),
        DateOnly d => PyFormat.IsoDate(d),
        TimeOnly t => PyFormat.IsoTime(t),
        TimeSpan t => TimeDeltaStr(t),
        _ => Convert.ToString(value, CultureInfo.InvariantCulture) ?? "",
    };

    /// <summary><c>str(datetime)</c>: <c>2026-10-01 09:30:00</c>, with <c>.ffffff</c> when there are microseconds.</summary>
    public static string DateTimeStr(DateTime value)
    {
        var micro = value.Ticks % TimeSpan.TicksPerSecond / 10;
        var text = value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        return micro == 0 ? text : $"{text}.{micro.ToString("D6", CultureInfo.InvariantCulture)}";
    }

    /// <summary><c>str(timedelta)</c>: <c>8:30:00</c>, <c>1 day, 2:00:00</c>, <c>-1 day, 23:00:00</c>.</summary>
    public static string TimeDeltaStr(TimeSpan value)
    {
        var ticks = value.Ticks / 10 * 10; // microseconds
        var days = (long)Math.Floor(ticks / (double)TimeSpan.TicksPerDay);
        var rest = ticks - days * TimeSpan.TicksPerDay;
        var seconds = rest / TimeSpan.TicksPerSecond;
        var micro = rest % TimeSpan.TicksPerSecond / 10;
        var clock = string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600}:{seconds / 60 % 60:00}:{seconds % 60:00}");
        if (micro != 0)
        {
            clock += "." + micro.ToString("D6", CultureInfo.InvariantCulture);
        }

        return days == 0 ? clock : string.Create(CultureInfo.InvariantCulture, $"{days} day{(Math.Abs(days) == 1 ? "" : "s")}, {clock}");
    }
}
