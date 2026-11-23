using System.Globalization;
using System.Numerics;

namespace Ats.Domain.Rules;

/// <summary>Python's rounding, which the figures must match to the last digit.</summary>
public static class PyMath
{
    /// <summary>
    /// <c>round(x, digits)</c>: rounds the exact binary value of <paramref name="x"/> to <paramref name="digits"/>
    /// decimals, ties to even, and returns the nearest double (CPython's correctly-rounded algorithm, so
    /// <c>round(2.675, 2) == 2.67</c>). Exact rational arithmetic; no scaling error.
    /// </summary>
    public static double Round(double x, int digits)
    {
        if (double.IsNaN(x) || double.IsInfinity(x) || x == 0)
        {
            return x;
        }

        var bits = BitConverter.DoubleToInt64Bits(x);
        var negative = bits < 0;
        var exponent = (int)((bits >> 52) & 0x7FF);
        var mantissa = bits & 0xFFFFFFFFFFFFFL;
        if (exponent == 0)
        {
            exponent = 1;
        }
        else
        {
            mantissa |= 1L << 52;
        }

        exponent -= 1075; // |x| = mantissa * 2^exponent
        BigInteger numerator = mantissa, denominator = BigInteger.One;
        if (exponent > 0)
        {
            numerator <<= exponent;
        }
        else
        {
            denominator <<= -exponent;
        }

        if (digits >= 0)
        {
            numerator *= BigInteger.Pow(10, digits);
        }
        else
        {
            denominator *= BigInteger.Pow(10, -digits);
        }

        var quotient = BigInteger.DivRem(numerator, denominator, out var remainder);
        var twice = remainder * 2;
        if (twice > denominator || (twice == denominator && !quotient.IsEven))
        {
            quotient += 1;
        }

        var text = (negative ? "-" : "") + quotient.ToString(CultureInfo.InvariantCulture) + "E" + (-digits).ToString(CultureInfo.InvariantCulture);
        return double.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture);
    }

    /// <summary><c>round(x)</c> without digits: the nearest integer, ties to even (exact on doubles).</summary>
    public static int RoundToInt(double x) => (int)Math.Round(x, MidpointRounding.ToEven);

    /// <summary>
    /// <c>int(Decimal(str(x)).quantize(Decimal("1"), rounding=ROUND_HALF_UP))</c> (wfo_rules.round_half_up):
    /// the shortest repr of the double, rounded half away from zero.
    /// </summary>
    public static int RoundHalfUp(double x)
    {
        var shortest = decimal.Parse(x.ToString("R", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
        return (int)Math.Round(shortest, 0, MidpointRounding.AwayFromZero);
    }
}

/// <summary>Office-local dates and times (<c>app/core/timeutil.py</c>).</summary>
public static class OfficeTime
{
    /// <summary>The office-local calendar date of an instant (<c>local_date</c>).</summary>
    public static DateOnly LocalDate(DateTimeOffset instant, TimeZoneInfo zone) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);

    /// <summary>
    /// A wall-clock time on a local date, as UTC (<c>local_to_utc</c>). Like zoneinfo with fold=0, a time in a DST
    /// gap or overlap takes the offset in force before the transition, so it never fails.
    /// </summary>
    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (zone.IsInvalidTime(local) || zone.IsAmbiguousTime(local))
        {
            offset = zone.GetUtcOffset(local.AddDays(-1));
            if (zone.IsAmbiguousTime(local) && !zone.GetAmbiguousTimeOffsets(local).Contains(offset))
            {
                offset = zone.GetAmbiguousTimeOffsets(local).Max();
            }
        }
        else
        {
            offset = zone.GetUtcOffset(local);
        }

        return new DateTimeOffset(local, offset).ToUniversalTime();
    }

    /// <summary>ISO weekday: 1 = Monday .. 7 = Sunday.</summary>
    public static int IsoWeekday(DateOnly d) => d.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)d.DayOfWeek;

    /// <summary>Python's <c>date.weekday()</c>: 0 = Monday .. 6 = Sunday.</summary>
    public static int Weekday(DateOnly d) => IsoWeekday(d) - 1;

    /// <summary>The last day of the date's month.</summary>
    public static DateOnly MonthEnd(DateOnly d) => new(d.Year, d.Month, DateTime.DaysInMonth(d.Year, d.Month));
}
