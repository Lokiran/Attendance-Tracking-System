namespace Ats.Domain.Rules;

// Port of backend/app/services/attendance_rules.py: the single source of truth for day statuses and period metrics.
// No database or framework access, so every rule is unit-testable. Rules (also in docs/ATTENDANCE_RULES.md):
// * OFFICE -> PRESENT; with recorded hours below min_present_minutes (on a past day) -> ABSENT (BELOW_MINIMUM).
//   Without hours the day is still PRESENT ("not recorded") and never short.
// * WFH -> WFH (a working day, not office attendance). LEAVE / HOLIDAY / WEEKOFF -> LEAVE / HOLIDAY / WEEKEND.
// * No entry on a scheduled, non-holiday working day: ABSENT when a committed timesheet covers that date for the
//   person, otherwise NO_TIMESHEET (not expected).
// * Expected = scheduled working day, not a holiday, on/after joining, and PRESENT, WFH or ABSENT. The rate is office
//   days out of expected days plus office days on a holiday or off day (counted_days).

public static class Exceptions
{
    public const string LateArrival = "LATE_ARRIVAL";
    public const string ShortHours = "SHORT_HOURS";
    public const string BelowMinimum = "BELOW_MINIMUM";
    public const string Absent = "ABSENT";
}

public sealed record PolicyData(int MinPresentMinutes = 0, int LateGraceMinutes = 15);

/// <summary>What the imported timesheets (and admin overrides) say about one employee-day.</summary>
public sealed record TimesheetDay(int EmployeeId, DateOnly Date, WorkMode? WorkMode, int? Minutes = null,
                                  DateTimeOffset? FirstIn = null, DateTimeOffset? LastOut = null);

public sealed record ScheduleData(int Id, int? EmployeeId, int? DepartmentId, TimeOnly StartTime, TimeOnly EndTime,
                                  int RequiredMinutes, IReadOnlySet<int> WorkingDays, DateOnly EffectiveFrom,
                                  DateOnly? EffectiveTo, string Name = "")
{
    public bool AppliesOn(DateOnly d) => EffectiveFrom <= d && (EffectiveTo is null || d <= EffectiveTo);
}

public sealed record HolidayData(DateOnly HolidayDate, string Name, int? DepartmentId);

public sealed record EmployeeData(int Id, int? DepartmentId, DateOnly DateJoined);

/// <summary>One employee-day. Shared between requests by the record cache: read-only.</summary>
public sealed record DayRecord(int EmployeeId, DateOnly Date, DayStatus Status, bool Expected, bool ScheduledWorkday,
                               int RequiredMinutes, int NetMinutes, bool HoursKnown, WorkMode? WorkMode,
                               DateTimeOffset? FirstIn, DateTimeOffset? LastOut, string? HolidayName,
                               IReadOnlyList<string> Exceptions);

public sealed class Summary
{
    public Summary(DateOnly start, DateOnly end) => (Start, End) = (start, end);

    public DateOnly Start { get; }

    public DateOnly End { get; }

    public int ExpectedDays { get; set; }

    public int PresentDays { get; set; }

    public int PresentExpectedDays { get; set; }

    public int ExtraPresentDays { get; set; }

    public int AbsentDays { get; set; }

    public int WfhDays { get; set; }

    public int LeaveDays { get; set; }

    public int HolidayDays { get; set; }

    public int WeekendDays { get; set; }

    public int NotScheduledDays { get; set; }

    public int NoTimesheetDays { get; set; }

    public int NetMinutes { get; set; }

    public int RequiredMinutes { get; set; }

    /// <summary>Present days with recorded hours.</summary>
    public int HoursDays { get; set; }

    /// <summary>Shift minutes of those days.</summary>
    public int HoursRequiredMinutes { get; set; }

    public int LateDays { get; set; }

    /// <summary>Recorded office minutes against the shift minutes of the same days.</summary>
    public int VarianceMinutes => NetMinutes - HoursRequiredMinutes;

    public int? AverageNetMinutes => HoursDays == 0 ? null : PyMath.RoundToInt((double)NetMinutes / HoursDays);

    /// <summary>Expected days plus office days on a holiday or off day (the WFO working days).</summary>
    public int CountedDays => ExpectedDays + ExtraPresentDays;

    public double? AttendancePercentage => AttendanceRules.AttendanceRate(PresentExpectedDays + ExtraPresentDays, CountedDays);
}

/// <summary>Per-computation memo shared by everyone computed with the same schedules, zone and policy.</summary>
public sealed class RulesMemo
{
    internal Dictionary<(string Kind, int? Key, DateOnly Day), ScheduleData?> Schedules { get; } = [];

    internal Dictionary<(int ScheduleId, DateOnly Day), DateTimeOffset> LateAfter { get; } = [];
}

public static class AttendanceRules
{
    /// <summary>Office days out of the days counted, to one decimal (Python round()).</summary>
    public static double? AttendanceRate(int present, int counted) =>
        counted == 0 ? null : PyMath.Round(present * 100.0 / counted, 1);

    /// <summary>Employee-specific beats department beats organisation default; then the latest effective_from, then id.</summary>
    public static ScheduleData? ResolveSchedule(IEnumerable<ScheduleData> schedules, EmployeeData employee, DateOnly d)
    {
        ArgumentNullException.ThrowIfNull(schedules);
        ArgumentNullException.ThrowIfNull(employee);
        (int Tier, DateOnly From, int Id)? best = null;
        ScheduleData? chosen = null;
        foreach (var s in schedules)
        {
            if (!s.AppliesOn(d))
            {
                continue;
            }

            int tier;
            if (s.EmployeeId is not null)
            {
                if (s.EmployeeId != employee.Id)
                {
                    continue;
                }

                tier = 3;
            }
            else if (s.DepartmentId is not null)
            {
                if (s.DepartmentId != employee.DepartmentId)
                {
                    continue;
                }

                tier = 2;
            }
            else
            {
                tier = 1;
            }

            var key = (tier, s.EffectiveFrom, s.Id);
            if (best is null || key.CompareTo(best.Value) > 0)
            {
                (best, chosen) = (key, s);
            }
        }

        return chosen;
    }

    /// <summary>
    /// Day records for start..min(end, today); future dates are never evaluated. <paramref name="covered"/> tells
    /// whether a committed timesheet covers a date for this person; <paramref name="shared"/> memoises schedule
    /// lookups and shift starts across everyone in one computation.
    /// </summary>
    public static List<DayRecord> BuildDayRecords(EmployeeData employee, DateOnly start, DateOnly end, DateOnly today,
                                                  TimeZoneInfo zone, IEnumerable<TimesheetDay> timesheet,
                                                  IReadOnlyList<ScheduleData> schedules, IEnumerable<HolidayData> holidays,
                                                  PolicyData policy, Func<DateOnly, bool>? covered = null,
                                                  RulesMemo? shared = null)
    {
        ArgumentNullException.ThrowIfNull(employee);
        ArgumentNullException.ThrowIfNull(schedules);
        if (today < end)
        {
            end = today;
        }

        if (end < start)
        {
            return [];
        }

        var byDay = new Dictionary<DateOnly, TimesheetDay>();
        foreach (var t in timesheet.Where(t => t.EmployeeId == employee.Id))
        {
            byDay[t.Date] = t; // last wins, as a dict comprehension
        }

        var holidayMap = new Dictionary<DateOnly, string>();
        foreach (var h in holidays)
        {
            if (h.DepartmentId is null || h.DepartmentId == employee.DepartmentId)
            {
                holidayMap.TryAdd(h.HolidayDate, h.Name); // setdefault: the first one wins (P2)
            }
        }

        var personal = schedules.Any(s => s.EmployeeId == employee.Id);
        var ctx = new Context(employee, today, zone, policy, holidayMap, schedules, shared ?? new RulesMemo(),
                              covered ?? (_ => true), personal ? ("emp", employee.Id) : ("dept", employee.DepartmentId));
        var records = new List<DayRecord>(end.DayNumber - start.DayNumber + 1);
        for (var d = start; d <= end; d = d.AddDays(1))
        {
            records.Add(DayRecordFor(ctx, d, byDay.GetValueOrDefault(d)));
        }

        return records;
    }

    private sealed record Context(EmployeeData Employee, DateOnly Today, TimeZoneInfo Zone, PolicyData Policy,
                                  Dictionary<DateOnly, string> HolidayMap, IReadOnlyList<ScheduleData> Schedules,
                                  RulesMemo Memo, Func<DateOnly, bool> Covered, (string Kind, int? Key) ScheduleKey)
    {
        public ScheduleData? ScheduleOn(DateOnly d)
        {
            var key = (ScheduleKey.Kind, ScheduleKey.Key, d);
            if (!Memo.Schedules.TryGetValue(key, out var found))
            {
                found = Memo.Schedules[key] = ResolveSchedule(Schedules, Employee, d);
            }

            return found;
        }

        public DateTimeOffset LateAfter(ScheduleData schedule, DateOnly d)
        {
            var key = (schedule.Id, d);
            if (!Memo.LateAfter.TryGetValue(key, out var found))
            {
                found = Memo.LateAfter[key] = OfficeTime.ToUtc(d, schedule.StartTime, Zone).AddMinutes(Policy.LateGraceMinutes);
            }

            return found;
        }
    }

    private static DayRecord DayRecordFor(Context ctx, DateOnly d, TimesheetDay? ts)
    {
        var (employee, today, policy) = (ctx.Employee, ctx.Today, ctx.Policy);
        var schedule = d >= employee.DateJoined ? ctx.ScheduleOn(d) : null;
        var workday = schedule is not null && schedule.WorkingDays.Contains(OfficeTime.IsoWeekday(d));
        var holiday = workday ? ctx.HolidayMap.GetValueOrDefault(d) : null;
        var holidayName = schedule is not null ? ctx.HolidayMap.GetValueOrDefault(d) : null;
        var working = workday && holiday is null;
        var required = working && schedule is not null ? schedule.RequiredMinutes : 0;
        var mode = ts is not null && d >= employee.DateJoined ? ts.WorkMode : null;

        DayStatus status;
        bool expected;
        if (mode is null)
        {
            string[] none = [];
            IReadOnlyList<string> exceptions = none;
            if (d < employee.DateJoined || schedule is null)
            {
                (status, expected) = (DayStatus.NotScheduled, false);
            }
            else if (working)
            {
                if (d < today && ctx.Covered(d))
                {
                    (status, expected, exceptions) = (DayStatus.Absent, true, [Exceptions.Absent]);
                }
                else
                {
                    (status, expected) = (DayStatus.NoTimesheet, false);
                }
            }
            else if (holiday is not null)
            {
                (status, expected) = (DayStatus.Holiday, false);
            }
            else
            {
                (status, expected) = (DayStatus.Weekend, false);
            }

            return new DayRecord(employee.Id, d, status, expected, workday, expected ? required : 0, 0, false, null, null, null,
                                 holidayName, exceptions);
        }

        var found = new List<string>();
        var (net, hoursKnown) = (0, false);
        if (mode == WorkMode.Office)
        {
            hoursKnown = ts!.Minutes is not null;
            net = ts.Minutes ?? 0;
            if (hoursKnown && d < today && policy.MinPresentMinutes != 0 && net < policy.MinPresentMinutes)
            {
                (status, expected) = (DayStatus.Absent, working);
                found.Add(Exceptions.BelowMinimum);
            }
            else
            {
                (status, expected) = (DayStatus.Present, working);
                if (expected && schedule is not null)
                {
                    if (ts.FirstIn is { } firstIn && firstIn > ctx.LateAfter(schedule, d))
                    {
                        found.Add(Exceptions.LateArrival);
                    }

                    if (hoursKnown && d < today && net < required)
                    {
                        found.Add(Exceptions.ShortHours);
                    }
                }
            }
        }
        else if (mode == WorkMode.Wfh)
        {
            (status, expected) = (DayStatus.Wfh, working);
        }
        else if (mode == WorkMode.Leave)
        {
            (status, expected) = (DayStatus.Leave, false);
        }
        else if (mode == WorkMode.Holiday)
        {
            (status, expected) = (DayStatus.Holiday, false);
        }
        else
        {
            (status, expected) = (DayStatus.Weekend, false); // WEEKOFF
        }

        return new DayRecord(employee.Id, d, status, expected, workday, expected ? required : 0, net, hoursKnown, mode,
                             ts!.FirstIn, ts.LastOut, holidayName, found);
    }

    public static Summary Summarize(IEnumerable<DayRecord> records, DateOnly start, DateOnly end)
    {
        ArgumentNullException.ThrowIfNull(records);
        var s = new Summary(start, end);
        foreach (var r in records)
        {
            s.NetMinutes += r.NetMinutes;
            s.RequiredMinutes += r.RequiredMinutes;
            if (r.Expected)
            {
                s.ExpectedDays++;
            }

            if (r.Exceptions.Contains(Exceptions.LateArrival))
            {
                s.LateDays++;
            }

            switch (r.Status)
            {
                case DayStatus.Present:
                    s.PresentDays++;
                    if (r.Expected)
                    {
                        s.PresentExpectedDays++;
                    }
                    else
                    {
                        s.ExtraPresentDays++;
                    }

                    if (r.HoursKnown)
                    {
                        s.HoursDays++;
                        s.HoursRequiredMinutes += r.RequiredMinutes;
                    }

                    break;
                case DayStatus.Absent: s.AbsentDays++; break;
                case DayStatus.Wfh: s.WfhDays++; break;
                case DayStatus.Leave: s.LeaveDays++; break;
                case DayStatus.Holiday: s.HolidayDays++; break;
                case DayStatus.Weekend: s.WeekendDays++; break;
                case DayStatus.NotScheduled: s.NotScheduledDays++; break;
                case DayStatus.NoTimesheet: s.NoTimesheetDays++; break;
                default: break;
            }
        }

        return s;
    }
}
