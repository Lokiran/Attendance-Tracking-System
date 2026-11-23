using System.Globalization;

namespace Ats.Domain.Rules;

// Port of backend/app/services/wfo_rules.py. Classification of one employee-day, in priority order:
// 0. An Admin override always wins (MANUAL_OVERRIDE). 1. The work-mode column of an imported file (UPLOAD_COLUMN).
// 2. Badge rows mean OFFICE (BADGE). 3. Office Wi-Fi means OFFICE (WIFI). 4. Office / WFH picked at check-in (CHECK_IN).
// 5. Worked hours with no office signal mean WFH (INFERRED). 6. Otherwise HOLIDAY / WEEKOFF from the calendar, or none.
// Policy: a WFO day is OFFICE with at least min_office_minutes (or no hours known at all); working days are scheduled
// non-holiday days not LEAVE / HOLIDAY / WEEKOFF plus days worked on an off day; only days before today count;
// requirements are prorated (round half up) per calendar month and per Monday-Sunday week.

/// <summary>Everything known about one employee-day. Null means "this source says nothing".</summary>
public sealed record DayEvidence(
    bool ScheduledWorkday = true, bool Holiday = false, WorkMode? Override = null, WorkMode? UploadMode = null,
    int? UploadMinutes = null, int? BadgeMinutes = null, int? WifiMinutes = null, int CheckinOfficeMinutes = 0,
    int CheckinWfhMinutes = 0, int CheckinUntaggedMinutes = 0, bool CheckinOfficeSeen = false, bool CheckinWfhSeen = false);

public sealed record DayClass(WorkMode? WorkMode, WfoSource? Source, int OfficeMinutes, int WfhMinutes,
                              IReadOnlyList<string> Conflicts, bool HoursKnown = true)
{
    public static DayClass None { get; } = new(null, null, 0, 0, []);
}

public sealed record WfoPolicy(int MinDaysPerWeek = 3, int MinDaysPerMonth = 12, int MinOfficeMinutes = 240);

/// <summary>A classified day as the policy sees it.</summary>
public sealed record PolicyDay(DateOnly Date, WorkMode? WorkMode, int OfficeMinutes, bool Scheduled, bool HoursKnown = true);

public sealed record WeekResult(DateOnly WeekStart, DateOnly WeekEnd, string Label, int WorkingDays, int WfoDays,
                                int Required, string Status, int Shortfall);

public sealed class WfoSummary
{
    public int WorkingDays { get; set; }

    public int StandardDays { get; set; }

    public int WfoDays { get; set; }

    public int WfhDays { get; set; }

    public int OfficeDaysBelowMinimum { get; set; }

    public int LeaveDays { get; set; }

    public int HolidayDays { get; set; }

    public int WeekoffDays { get; set; }

    public int RequiredDays { get; set; }

    public int ShortfallDays { get; set; }

    public double? WfoPercentage { get; set; }

    public string Compliance { get; set; } = "COMPLIANT";

    public List<WeekResult> Weeks { get; } = [];
}

public static class WfoRules
{
    public const int NearDays = 3; // up to this many WFO days short is "near" (orange), more is "below" (red)

    private static readonly HashSet<WorkMode> NotWorking = [WorkMode.Leave, WorkMode.Holiday, WorkMode.Weekoff];

    // ------------------------------------------------------------------ Phase 1: classification

    private static bool HoursKnown(DayEvidence e, List<(WfoSource Source, int Minutes)> office) =>
        e.UploadMinutes is not null || office.Any(o => o.Minutes > 0) || e.CheckinOfficeMinutes > 0
        || e.CheckinWfhMinutes > 0 || e.CheckinUntaggedMinutes > 0;

    /// <summary>Office signals with their minutes, highest priority first.</summary>
    private static List<(WfoSource Source, int Minutes)> OfficeSignals(DayEvidence e)
    {
        var signals = new List<(WfoSource, int)>();
        if (e.UploadMode == WorkMode.Office)
        {
            signals.Add((WfoSource.UploadColumn, e.UploadMinutes ?? 0));
        }

        if (e.BadgeMinutes is { } badge)
        {
            signals.Add((WfoSource.Badge, badge));
        }

        if (e.WifiMinutes is { } wifi && wifi != 0)
        {
            signals.Add((WfoSource.Wifi, wifi));
        }

        if (e.CheckinOfficeSeen || e.CheckinOfficeMinutes != 0)
        {
            signals.Add((WfoSource.CheckIn, e.CheckinOfficeMinutes));
        }

        return signals;
    }

    /// <summary>Sources describe the same time in the office: the longest, never the sum.</summary>
    private static int OfficeMinutes(List<(WfoSource Source, int Minutes)> signals) =>
        signals.Count == 0 ? 0 : signals.Max(s => s.Minutes);

    private static int WfhMinutes(DayEvidence e)
    {
        var fromCheckins = e.CheckinWfhMinutes + e.CheckinUntaggedMinutes;
        var fromUpload = e.UploadMode == WorkMode.Wfh ? e.UploadMinutes ?? 0 : 0;
        return Math.Max(fromCheckins, fromUpload);
    }

    public static DayClass ClassifyDay(DayEvidence e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var office = OfficeSignals(e);
        if (e.Override is { } mode)
        {
            // The Admin states where the day was worked, so all of its worked time belongs to that mode.
            var worked = Math.Max(Math.Max(OfficeMinutes(office), e.CheckinOfficeMinutes + e.CheckinWfhMinutes + e.CheckinUntaggedMinutes),
                                  e.UploadMinutes ?? 0);
            return new DayClass(mode, WfoSource.ManualOverride, mode == WorkMode.Office ? worked : 0,
                                mode == WorkMode.Wfh ? worked : 0, [], HoursKnown(e, office));
        }

        if (office.Count > 0)
        {
            var source = office[0].Source;
            var conflicts = new List<string>();
            if (e.UploadMode is { } upload && upload != WorkMode.Office)
            {
                conflicts.Add($"UPLOAD_COLUMN said {EnumText.Of(upload)}; office signal from {EnumText.Of(source)}");
            }

            if (e.CheckinWfhSeen || e.CheckinWfhMinutes != 0)
            {
                conflicts.Add($"CHECK_IN said WFH; office signal from {EnumText.Of(source)}");
            }

            return new DayClass(WorkMode.Office, source, OfficeMinutes(office), e.CheckinWfhMinutes, conflicts, HoursKnown(e, office));
        }

        if (e.UploadMode is { } notWorking && NotWorking.Contains(notWorking))
        {
            var conflicts = new List<string>();
            var worked = WfhMinutes(e); // upload mode is not WFH here
            if (worked != 0)
            {
                conflicts.Add($"UPLOAD_COLUMN said {EnumText.Of(notWorking)} but {worked.ToString(CultureInfo.InvariantCulture)} minutes were checked in");
            }

            return new DayClass(notWorking, WfoSource.UploadColumn, 0, 0, conflicts);
        }

        if (e.UploadMode == WorkMode.Wfh)
        {
            return new DayClass(WorkMode.Wfh, WfoSource.UploadColumn, 0, WfhMinutes(e), []);
        }

        if (e.CheckinWfhSeen || e.CheckinWfhMinutes != 0)
        {
            return new DayClass(WorkMode.Wfh, WfoSource.CheckIn, 0, WfhMinutes(e), []);
        }

        if (e.CheckinUntaggedMinutes != 0 || (e.UploadMinutes ?? 0) != 0)
        {
            // Hours were worked but nothing places them in the office.
            return new DayClass(WorkMode.Wfh, WfoSource.Inferred, 0, Math.Max(e.CheckinUntaggedMinutes, e.UploadMinutes ?? 0), []);
        }

        if (e.Holiday)
        {
            return new DayClass(WorkMode.Holiday, WfoSource.Calendar, 0, 0, []);
        }

        if (!e.ScheduledWorkday)
        {
            return new DayClass(WorkMode.Weekoff, WfoSource.Calendar, 0, 0, []);
        }

        return DayClass.None;
    }

    /// <summary>attendance_daily.status for a classified day (how much was worked; where is the work mode).</summary>
    public static DailyStatus DayStatusOf(DayClass c, int fullDayMinutes, int halfDayMinutes)
    {
        ArgumentNullException.ThrowIfNull(c);
        switch (c.WorkMode)
        {
            case WorkMode.Leave: return DailyStatus.Leave;
            case WorkMode.Holiday: return DailyStatus.Holiday;
            case WorkMode.Weekoff: return DailyStatus.Weekend;
            default: break;
        }

        var worked = c.OfficeMinutes + c.WfhMinutes;
        if (!c.HoursKnown && c.WorkMode is WorkMode.Office or WorkMode.Wfh)
        {
            return DailyStatus.Present; // listed as worked, hours not supplied
        }

        if (worked >= fullDayMinutes)
        {
            return DailyStatus.Present;
        }

        return worked >= halfDayMinutes && worked > 0 ? DailyStatus.HalfDay : DailyStatus.Absent;
    }

    // ------------------------------------------------------------------ Phase 2: policy

    public static bool IsWfoDay(PolicyDay d, WfoPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(d);
        ArgumentNullException.ThrowIfNull(policy);
        return d.WorkMode == WorkMode.Office && (!d.HoursKnown || d.OfficeMinutes >= policy.MinOfficeMinutes);
    }

    public static bool IsWorkingDay(PolicyDay d)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (d.WorkMode is { } m && NotWorking.Contains(m))
        {
            return false;
        }

        return d.Scheduled || d.WorkMode is WorkMode.Office or WorkMode.Wfh;
    }

    /// <summary>Scheduled and not leave / holiday / weekly off: the days that size a requirement.</summary>
    public static bool IsScheduledWorkingDay(PolicyDay d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return d.Scheduled && !(d.WorkMode is { } m && NotWorking.Contains(m));
    }

    /// <summary>MET at or above the requirement, NEAR up to <paramref name="near"/> days short, BELOW otherwise.</summary>
    public static string WfoBand(int wfoDays, int requiredDays, int near = NearDays)
    {
        if (wfoDays >= requiredDays)
        {
            return "MET";
        }

        return wfoDays >= requiredDays - near ? "NEAR" : "BELOW";
    }

    /// <summary>(first day, last day) of every calendar month overlapping start..end.</summary>
    public static IEnumerable<(DateOnly First, DateOnly Last)> CalendarMonths(DateOnly start, DateOnly end)
    {
        for (var first = new DateOnly(start.Year, start.Month, 1); first <= end; first = first.AddMonths(1))
        {
            yield return (first, OfficeTime.MonthEnd(first));
        }
    }

    private static IEnumerable<DateOnly> Dates(DateOnly start, DateOnly end)
    {
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            yield return d;
        }
    }

    /// <summary>
    /// Policy figures for start..end. <paramref name="days"/> may include today, future days and six days either side
    /// (weeks are judged on all seven days); only days before today count. <paramref name="isScheduled"/> says whether
    /// the schedule has work on a date (ignoring holidays), for any date.
    /// </summary>
    public static WfoSummary SummarizeWfo(IEnumerable<PolicyDay> days, DateOnly start, DateOnly end, DateOnly today,
                                          WfoPolicy policy, Func<DateOnly, bool> isScheduled)
    {
        ArgumentNullException.ThrowIfNull(days);
        ArgumentNullException.ThrowIfNull(isScheduled);
        var byDate = new Dictionary<DateOnly, PolicyDay>();
        foreach (var d in days)
        {
            byDate[d.Date] = d;
        }

        var s = new WfoSummary
        {
            StandardDays = Dates(start, end).Count(d => byDate.TryGetValue(d, out var p) && p.Scheduled),
        };
        var completed = Dates(start, Min(end, today.AddDays(-1))).Where(byDate.ContainsKey).Select(d => byDate[d]).ToList();
        foreach (var d in completed)
        {
            if (IsWorkingDay(d))
            {
                s.WorkingDays++;
            }

            if (IsWfoDay(d, policy))
            {
                s.WfoDays++;
            }
            else if (d.WorkMode == WorkMode.Office)
            {
                s.OfficeDaysBelowMinimum++;
            }

            switch (d.WorkMode)
            {
                case WorkMode.Wfh: s.WfhDays++; break;
                case WorkMode.Leave: s.LeaveDays++; break;
                case WorkMode.Holiday: s.HolidayDays++; break;
                case WorkMode.Weekoff: s.WeekoffDays++; break;
                default: break;
            }
        }

        // Per calendar month: min_days_per_month scaled by how many of the month's standard days are completed
        // scheduled working days of the period; a multi-month period adds them up.
        foreach (var (mStart, mEnd) in CalendarMonths(start, end))
        {
            var standard = Dates(mStart, mEnd).Count(d => byDate.TryGetValue(d, out var p) ? p.Scheduled : isScheduled(d));
            var done = completed.Count(d => mStart <= d.Date && d.Date <= mEnd && IsScheduledWorkingDay(d));
            if (standard != 0)
            {
                s.RequiredDays += Math.Min(done, PyMath.RoundHalfUp((double)policy.MinDaysPerMonth * done / standard));
            }
        }

        var monthlyShort = Math.Max(0, s.RequiredDays - s.WfoDays);
        var weekShortTotal = 0;
        for (var week = start.AddDays(-OfficeTime.Weekday(start)); week <= end; week = week.AddDays(7))
        {
            var weekEnd = week.AddDays(6);
            var counted = Dates(week, weekEnd).Where(d => d < today && byDate.ContainsKey(d)).Select(d => byDate[d]).ToList();
            var working = counted.Count(IsWorkingDay);
            var sized = counted.Count(IsScheduledWorkingDay);
            var wfo = counted.Count(d => IsWfoDay(d, policy));
            var nominal = Dates(week, weekEnd).Count(isScheduled);
            var required = nominal != 0 ? Math.Min(sized, PyMath.RoundHalfUp((double)policy.MinDaysPerWeek * sized / nominal)) : 0;
            var upcoming = week > today;
            var inProgress = !upcoming && weekEnd >= today;
            var shortfall = inProgress || upcoming ? 0 : Math.Max(0, required - wfo);
            weekShortTotal += shortfall;
            var status = upcoming ? "UPCOMING" : inProgress ? "IN_PROGRESS" : shortfall != 0 ? "SHORT" : "MET";
            s.Weeks.Add(new WeekResult(week, weekEnd, $"{DayMonth(week)} – {DayMonth(weekEnd)}", working, wfo, required, status, shortfall));
        }

        s.ShortfallDays = Math.Max(monthlyShort, weekShortTotal);
        s.Compliance = monthlyShort != 0 || weekShortTotal != 0 ? "NON_COMPLIANT" : "COMPLIANT";
        s.WfoPercentage = s.WorkingDays != 0 ? PyMath.Round(s.WfoDays * 100.0 / s.WorkingDays, 1) : null;
        return s;
    }

    /// <summary><c>f"{d:%d %b}"</c>: "05 Oct" (English month abbreviations, as on every current host; P9).</summary>
    public static string DayMonth(DateOnly d) => d.ToString("dd MMM", CultureInfo.InvariantCulture);

    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
}
