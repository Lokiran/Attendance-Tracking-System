using System.Text.RegularExpressions;

namespace Ats.Application.Uploads;

/// <summary>
/// How the Python readers turn an Excel serial number into a value: openpyxl for .xlsx (its date / timedelta format
/// rules and <c>from_excel</c>) and xlrd for .xls (<c>xldate_as_datetime</c>, a value below one day as a time).
/// </summary>
internal static partial class ExcelFormats
{
    private static readonly DateTime WindowsEpoch = new(1899, 12, 30);

    /// <summary>
    /// The serial number behind an ExcelDataReader date. Like openpyxl and xlrd, it counts serials below 60 from 31 December
    /// 1899 (Excel's 1900 calendar), so a time-only cell (0.75) arrives as 31 December 1899 18:00, one day after the OLE epoch.
    /// </summary>
    public static double SerialOf(DateTime value)
    {
        var oa = value.ToOADate();
        return oa < 61 ? oa - 1 : oa;
    }

    /// <summary>openpyxl <c>is_date_format</c>: a d / m / h / y / s outside quotes and locale brackets.</summary>
    public static bool IsDateFormat(string? format)
    {
        if (format is null)
        {
            return false;
        }

        var first = Strip().Replace(format.Split(';')[0], "");
        return DateLetter().IsMatch(first);
    }

    /// <summary>openpyxl <c>is_timedelta_format</c>: <c>[h]:mm</c>, <c>[mm]:ss</c>, <c>[ss]</c>.</summary>
    public static bool IsTimedeltaFormat(string? format) => format is not null && Timedelta().IsMatch(format.Split(';')[0]);

    /// <summary>openpyxl <c>from_excel</c> (1900 date system): a time below one day, else a datetime.</summary>
    public static object FromExcel(double value)
    {
        var (day, fraction) = DivMod(value);
        var ms = (long)Math.Round(fraction * 86400 * 1000, MidpointRounding.ToEven);
        try
        {
            if (value is >= 0 and < 1 && ms < 86_400_000)
            {
                return new TimeOnly(ms * TimeSpan.TicksPerMillisecond);
            }

            if (value is > 0 and < 60)
            {
                day += 1;
            }

            return WindowsEpoch.AddDays(day).AddTicks(ms * TimeSpan.TicksPerMillisecond);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "#VALUE!"; // openpyxl: a date outside the limits is treated as an error cell
        }
    }

    /// <summary>openpyxl <c>from_excel</c> for a 1904-based workbook (no 1900 leap-year adjustment).</summary>
    public static object FromExcel1904(double value)
    {
        var (day, fraction) = DivMod(value);
        var ms = (long)Math.Round(fraction * 86400 * 1000, MidpointRounding.ToEven);
        if (value is >= 0 and < 1 && ms < 86_400_000)
        {
            return new TimeOnly(ms * TimeSpan.TicksPerMillisecond);
        }

        try
        {
            return new DateTime(1904, 1, 1).AddDays(day).AddTicks(ms * TimeSpan.TicksPerMillisecond);
        }
        catch (ArgumentOutOfRangeException)
        {
            return "#VALUE!";
        }
    }

    /// <summary>openpyxl <c>from_excel(timedelta=True)</c>: a duration rounded to milliseconds.</summary>
    public static TimeSpan TimeDelta(double value)
    {
        var micro = (long)Math.Round(value * 86400.0 * 1_000_000, MidpointRounding.ToEven);
        var seconds = (long)Math.Floor(micro / 1_000_000.0);
        var rest = micro - seconds * 1_000_000;
        if (rest != 0)
        {
            rest = (long)Math.Round(rest / 1000.0, MidpointRounding.ToEven) * 1000;
        }

        return TimeSpan.FromTicks((seconds * 1_000_000 + rest) * 10);
    }

    /// <summary>xlrd <c>xldate_as_datetime</c> (datemode 0), then <c>.time()</c> for a value below one day.</summary>
    public static object XlrdDate(double value)
    {
        if (value < 0)
        {
            return value; // XLDateNegative: the cell stays a number
        }

        var epoch = value < 60 ? new DateTime(1899, 12, 31) : WindowsEpoch;
        var days = Math.Truncate(value);
        var ms = (long)Math.Round((value - days) * 86400000.0, MidpointRounding.ToEven);
        var result = epoch.AddDays(days).AddTicks(ms * TimeSpan.TicksPerMillisecond);
        return value < 1 ? TimeOnly.FromDateTime(result) : result;
    }

    /// <summary>Python <c>divmod(value, 1)</c> for a float.</summary>
    private static (double Day, double Fraction) DivMod(double value)
    {
        var mod = value % 1.0;
        if (mod != 0 && mod < 0)
        {
            mod += 1.0;
        }

        return (Math.Round(value - mod), mod);
    }

    [GeneratedRegex("\".*?\"|\\[(?!hh?\\]|mm?\\]|ss?\\])[^\\]]*\\]")]
    private static partial Regex Strip();

    [GeneratedRegex(@"(?<![_\\])[dmhysDMHYS]")]
    private static partial Regex DateLetter();

    [GeneratedRegex(@"\[hh?\](:mm(:ss(\.0*)?)?)?|\[mm?\](:ss(\.0*)?)?|\[ss?\](\.0*)?", RegexOptions.IgnoreCase)]
    private static partial Regex Timedelta();
}
