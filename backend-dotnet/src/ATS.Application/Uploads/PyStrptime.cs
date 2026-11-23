using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Ats.Application.Uploads;

/// <summary>
/// <c>datetime.strptime</c> (CPython 3.14 <c>_strptime</c>, C locale) for the directives the timesheet import uses:
/// <c>%Y %m %d %b %B %H %I %M %S %p</c>. The whole text must match, case-insensitively; whitespace in the format
/// matches any run of whitespace; an impossible date or time (31 February, second 60) is no match.
/// </summary>
public static class PyStrptime
{
    private static readonly string[] MonthNames =
        ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    private static readonly ConcurrentDictionary<string, Regex> Patterns = new(StringComparer.Ordinal);

    public static DateOnly? Date(string text, string format)
    {
        var m = Match(text, format);
        if (m is null)
        {
            return null;
        }

        var year = Group(m, "Y") ?? 1900;
        int month;
        if (m.Groups["m"].Success)
        {
            month = Group(m, "m")!.Value;
        }
        else if (m.Groups["b"].Success || m.Groups["B"].Success)
        {
            var name = (m.Groups["b"].Success ? m.Groups["b"].Value : m.Groups["B"].Value).ToLowerInvariant();
            month = Array.FindIndex(MonthNames, n => n == name || n[..3] == name) + 1;
        }
        else
        {
            month = 1;
        }

        var day = Group(m, "d") ?? 1;
        return day <= DateTime.DaysInMonth(year, month) ? new DateOnly(year, month, day) : null;
    }

    public static TimeOnly? Time(string text, string format)
    {
        var m = Match(text, format);
        if (m is null)
        {
            return null;
        }

        var hour = Group(m, "H") ?? 0;
        if (m.Groups["I"].Success)
        {
            hour = Group(m, "I")!.Value;
            var ampm = m.Groups["p"].Success ? m.Groups["p"].Value.ToLowerInvariant() : "";
            if (ampm is "" or "am")
            {
                hour = hour == 12 ? 0 : hour;
            }
            else if (hour != 12)
            {
                hour += 12;
            }
        }

        var minute = Group(m, "M") ?? 0;
        var second = Group(m, "S") ?? 0;
        return second <= 59 ? new TimeOnly(hour, minute, second) : null;
    }

    private static int? Group(Match m, string name) =>
        m.Groups[name].Success ? int.Parse(m.Groups[name].Value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture) : null;

    private static Match? Match(string text, string format)
    {
        ArgumentNullException.ThrowIfNull(text);
        var regex = Patterns.GetOrAdd(format, Build);
        var m = regex.Match(text);
        return m.Success ? m : null;
    }

    private static Regex Build(string format)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if (c == '%' && i + 1 < format.Length)
            {
                sb.Append(Directive(format[++i]));
            }
            else if (PyText.IsSpace(c))
            {
                while (i + 1 < format.Length && PyText.IsSpace(format[i + 1]))
                {
                    i++;
                }

                sb.Append(@"[\s\x1c-\x1f]+");
            }
            else
            {
                sb.Append(Regex.Escape(c.ToString()));
            }
        }

        return new Regex(sb.Append(@"\z").ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    private static string Directive(char d) => d switch
    {
        'd' => "(?<d>3[0-1]|[1-2][0-9]|0[1-9]|[1-9]| [1-9])",
        'H' => "(?<H>2[0-3]|[0-1][0-9]|[0-9]| [0-9])",
        'I' => "(?<I>1[0-2]|0[1-9]|[1-9]| [1-9])",
        'm' => "(?<m>1[0-2]|0[1-9]|[1-9])",
        'M' => "(?<M>[0-5][0-9]|[0-9])",
        'S' => "(?<S>6[0-1]|[0-5][0-9]|[0-9])",
        'Y' => "(?<Y>[0-9][0-9][0-9][0-9])",
        'b' => "(?<b>" + string.Join('|', MonthNames.Select(n => n[..3])) + ")",
        'B' => "(?<B>" + string.Join('|', MonthNames.OrderByDescending(n => n.Length)) + ")",
        'p' => "(?<p>am|pm)",
        _ => throw new ArgumentException($"Unsupported strptime directive %{d}", nameof(d)),
    };
}
