using System.Globalization;
using System.Text.RegularExpressions;
using Ats.Application.Python;

namespace Ats.Application.Uploads;

/// <summary>A row problem (Python's ValueError inside <c>parse_rows</c>): its message is shown with the row.</summary>
public sealed class RowValueException : Exception
{
    public RowValueException()
        : this("invalid value")
    {
    }

    public RowValueException(string message)
        : base(message)
    {
    }

    public RowValueException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>Reading dates, hours and times from cells (<c>wfo_upload.py</c>: parse_date, DateReader, parse_hours, parse_time).</summary>
public static partial class UploadValues
{
    public static readonly string[] DateFormats =
        ["%Y-%m-%d", "%d-%m-%Y", "%d/%m/%Y", "%d.%m.%Y", "%d-%b-%Y", "%d %b %Y", "%d-%B-%Y", "%d %B %Y", "%Y/%m/%d"];

    public static readonly string[] TimeFormats = ["%H:%M", "%H:%M:%S", "%I:%M %p", "%I:%M:%S %p", "%I %p"];

    /// <summary>One date, day-first for text (31/03/2026, 31-03-2026, 2026-03-31, 31-Mar-2026) or an Excel date.</summary>
    public static DateOnly? ParseDate(object? v)
    {
        switch (v)
        {
            case DateTime dt:
                return DateOnly.FromDateTime(dt);
            case DateOnly d:
                return d;
        }

        var text = PyText.CellText(v);
        foreach (var format in DateFormats)
        {
            if (PyStrptime.Date(text, format) is { } d)
            {
                return d;
            }
        }

        return null;
    }

    /// <summary>
    /// Every reading of a date cell as (date, swapped). "15/09/2026" has one reading; "09-01-2026" has two. An Excel date
    /// with a day up to 12 has two as well: as stored, and with day and month swapped.
    /// </summary>
    public static List<(DateOnly Date, bool Swapped)> DateCandidates(object? v)
    {
        if (v is DateTime or DateOnly)
        {
            var d = ParseDate(v)!.Value;
            return d.Day <= 12 && d.Day != d.Month ? [(d, false), (new DateOnly(d.Year, d.Day, d.Month), true)] : [(d, false)];
        }

        var text = PyText.CellText(v);
        var m = NumericDate().Match(text);
        if (!m.Success)
        {
            return ParseDate(text) is { } parsed ? [(parsed, false)] : [];
        }

        int a = Digits(m.Groups[1].Value), b = Digits(m.Groups[2].Value), y = Digits(m.Groups[3].Value);
        var output = new List<(DateOnly, bool)>();
        foreach (var (day, month, monthFirst) in new[] { (a, b, false), (b, a, true) })
        {
            if (y is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(y, month))
            {
                continue;
            }

            var d = new DateOnly(y, month, day);
            if (output.All(x => x.Item1 != d))
            {
                output.Add((d, monthFirst));
            }
        }

        return output;
    }

    /// <summary>Minutes from 7.5 / "7.5" / "7:30" / an Excel time. Null if empty; a row problem if invalid.</summary>
    public static int? ParseHours(object? v)
    {
        if (v is null || PyText.CellText(v).Length == 0)
        {
            return null;
        }

        long minutes;
        switch (v)
        {
            case TimeOnly t:
                return t.Hour * 60 + t.Minute;
            case TimeSpan span:
                minutes = (long)Math.Floor(span.Ticks / (double)TimeSpan.TicksPerSecond / 60);
                break;
            case double or bool:
                minutes = RoundToMinutes(v is bool b ? (b ? 1 : 0) : (double)v);
                break;
            default:
            {
                var text = PyText.Strip(PyText.CellText(v).ToLowerInvariant().Replace("hrs", "", StringComparison.Ordinal)
                                            .Replace("h", "", StringComparison.Ordinal));
                var match = HoursClock().Match(text);
                minutes = match.Success ? Digits(match.Groups[1].Value) * 60L + Digits(match.Groups[2].Value) : RoundToMinutes(PyFloat(text));
                break;
            }
        }

        if (minutes is < 0 or > 24 * 60)
        {
            throw new RowValueException("hours must be between 0 and 24");
        }

        return (int)minutes;
    }

    public static TimeOnly? ParseTime(object? v)
    {
        if (v is null || PyText.CellText(v).Length == 0)
        {
            return null;
        }

        switch (v)
        {
            case DateTime dt:
                return TimeOnly.FromDateTime(dt);
            case TimeOnly t:
                return t;
        }

        var text = PyText.CellText(v).ToUpperInvariant();
        foreach (var format in TimeFormats)
        {
            if (PyStrptime.Time(text, format) is { } t)
            {
                return t;
            }
        }

        throw new RowValueException($"time {PyFormat.Repr(text)} is not like 09:30 or 6:15 PM");
    }

    /// <summary><c>round(x * 60)</c>: half to even; NaN is a row problem as in Python.</summary>
    private static long RoundToMinutes(double hours)
    {
        var x = hours * 60;
        if (double.IsNaN(x))
        {
            throw new RowValueException("cannot convert float NaN to integer");
        }

        if (double.IsInfinity(x))
        {
            // TODO(parity): Python raises OverflowError here, which is a server error; a row problem is kinder.
            throw new RowValueException("cannot convert float infinity to integer");
        }

        return (long)Math.Round(x, MidpointRounding.ToEven);
    }

    /// <summary>Python <c>float(text)</c>: decimal / exponent / inf / nan, '_' between digits; else its ValueError.</summary>
    internal static double PyFloat(string text)
    {
        var s = PyText.Strip(text);
        var candidate = PyFloatText().IsMatch(s) ? s.Replace("_", "", StringComparison.Ordinal) : null;
        if (candidate is not null)
        {
            var lower = candidate.ToLowerInvariant().TrimStart('+');
            var negative = lower.StartsWith('-');
            var body = negative ? lower[1..] : lower;
            if (body is "inf" or "infinity")
            {
                return negative ? double.NegativeInfinity : double.PositiveInfinity;
            }

            if (body == "nan")
            {
                return double.NaN;
            }

            if (double.TryParse(candidate, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        throw new RowValueException($"could not convert string to float: {PyFormat.Repr(text)}");
    }

    private static int Digits(string text)
    {
        var n = 0;
        foreach (var c in text)
        {
            n = n * 10 + (int)char.GetNumericValue(c);
        }

        return n;
    }

    [GeneratedRegex(@"^(\d{1,2})[-/.](\d{1,2})[-/.](\d{4})$")]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"^(\d{1,2}):(\d{2})(?::\d{2})?\z")]
    private static partial Regex HoursClock();

    [GeneratedRegex(@"^[+-]?((\d(_?\d)*)?\.?\d(_?\d)*([eE][+-]?\d(_?\d)*)?|\d(_?\d)*\.([eE][+-]?\d(_?\d)*)?|inf|infinity|nan)\z",
                    RegexOptions.IgnoreCase)]
    private static partial Regex PyFloatText();
}

/// <summary>
/// Reads dates using the months the rest of the file is in (<c>DateReader</c>): unambiguous dates show which months the file
/// covers, and an ambiguous date takes the reading that falls in those months (otherwise day-first for text, as stored
/// for an Excel date).
/// </summary>
public sealed class DateReader
{
    private DateReader(HashSet<(int Year, int Month)> months) => Months = months;

    public HashSet<(int Year, int Month)> Months { get; }

    public int MonthFirst { get; private set; }

    public int AmbiguousDayFirst { get; private set; }

    public static DateReader ForValues(IEnumerable<object?> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var counts = new Dictionary<(int, int), int>();
        foreach (var v in values)
        {
            var c = UploadValues.DateCandidates(v);
            if (c.Count == 1)
            {
                var key = (c[0].Date.Year, c[0].Date.Month);
                counts[key] = counts.GetValueOrDefault(key) + 1;
            }
        }

        var total = counts.Values.Sum();
        return new DateReader([.. counts.Where(kv => total > 0 && kv.Value >= 0.1 * total).Select(kv => kv.Key)]);
    }

    public DateOnly? Read(object? v)
    {
        var c = UploadValues.DateCandidates(v);
        if (c.Count == 0)
        {
            return null;
        }

        if (c.Count == 1)
        {
            return c[0].Date;
        }

        var fitting = c.Where(x => Months.Contains((x.Date.Year, x.Date.Month))).ToList();
        if (fitting.Count == 1)
        {
            if (fitting[0].Swapped)
            {
                MonthFirst++;
            }

            return fitting[0].Date;
        }

        if (v is not (DateTime or DateOnly))
        {
            AmbiguousDayFirst++; // an Excel date stays as stored; a text date is read day-first
        }

        return c.First(x => !x.Swapped).Date;
    }
}
