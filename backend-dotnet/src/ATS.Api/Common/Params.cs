using Ats.Application.Common;
using Ats.Application.Configuration;

namespace Ats.Api.Common;

/// <summary>The shared parameters of backend/app/api/deps.py: the date range and paging.</summary>
internal static class Params
{
    /// <summary>
    /// <c>start</c> / <c>end</c> (office-local, inclusive): end defaults to today, start to the 1st of end's month. When both
    /// parse, the range is checked at once (Python's DateRange raises inside the dependency, before later parameters).
    /// </summary>
    public static (DateOnly Start, DateOnly End) Range(QueryReader q, DateOnly today, AtsSettings settings)
    {
        var before = q.ErrorCount;
        var start = q.Date("start");
        var end = q.Date("end");
        if (q.ErrorCount > before)
        {
            return default; // the dependency is not called; the problems are reported with the others
        }

        var e = end ?? today;
        var s = start ?? new DateOnly(e.Year, e.Month, 1);
        if (e < s)
        {
            throw new ValidationFailedException("End date must be on or after start date");
        }

        if (e.DayNumber - s.DayNumber + 1 > settings.MaxReportDays)
        {
            throw new ValidationFailedException($"Date range cannot exceed {settings.MaxReportDays} days");
        }

        return (s, e);
    }

    /// <summary><c>page</c> (>= 1, default 1) and <c>page_size</c> (1-200, default 20).</summary>
    public static (int Page, int PageSize) Paging(QueryReader q) =>
        (q.Int("page", 1, ge: 1), q.Int("page_size", 20, ge: 1, le: 200));
}
