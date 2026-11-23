using Ats.Application.Wfo;
using Ats.Domain;
using Ats.Domain.Rules;

namespace Ats.Application.Attendance;

// Shapes of backend/app/schemas/attendance.py (field order as declared; snake_case on the wire).

public sealed record DayRecordOut(DateOnly Date, DayStatus Status, bool Expected, bool ScheduledWorkday, int RequiredMinutes,
                                  int NetMinutes, bool HoursKnown, WorkMode? WorkMode, DateTimeOffset? FirstIn, DateTimeOffset? LastOut,
                                  string? HolidayName, IReadOnlyList<string> Exceptions)
{
    public static DayRecordOut Of(DayRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        return new(r.Date, r.Status, r.Expected, r.ScheduledWorkday, r.RequiredMinutes, r.NetMinutes, r.HoursKnown, r.WorkMode,
                   r.FirstIn, r.LastOut, r.HolidayName, [.. r.Exceptions]);
    }
}

public sealed record SummaryOut(DateOnly Start, DateOnly End, int ExpectedDays, int PresentDays, int PresentExpectedDays,
                                int ExtraPresentDays, int AbsentDays, int WfhDays, int LeaveDays, int HolidayDays, int WeekendDays,
                                int NotScheduledDays, int NoTimesheetDays, int NetMinutes, int RequiredMinutes, int HoursDays,
                                int HoursRequiredMinutes, int VarianceMinutes, int? AverageNetMinutes, double? AttendancePercentage,
                                int CountedDays, int LateDays, WfoRangeSummary? Wfo)
{
    public static SummaryOut Of(Summary s, WfoRangeSummary? wfo)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.Start, s.End, s.ExpectedDays, s.PresentDays, s.PresentExpectedDays, s.ExtraPresentDays, s.AbsentDays, s.WfhDays,
                   s.LeaveDays, s.HolidayDays, s.WeekendDays, s.NotScheduledDays, s.NoTimesheetDays, s.NetMinutes, s.RequiredMinutes,
                   s.HoursDays, s.HoursRequiredMinutes, s.VarianceMinutes, s.AverageNetMinutes, s.AttendancePercentage, s.CountedDays,
                   s.LateDays, wfo);
    }
}

public sealed record ScheduleBrief(string Name, TimeOnly StartTime, TimeOnly EndTime, int RequiredMinutes, IReadOnlyList<int> WorkingDays)
{
    public static ScheduleBrief? Of(ScheduleData? s) =>
        s is null ? null : new(s.Name, s.StartTime, s.EndTime, s.RequiredMinutes, [.. s.WorkingDays.Order()]);
}

public sealed record CurrentStatusOut(DateTimeOffset ServerTime, DateOnly Today, string OfficeTimezone, ScheduleBrief? Schedule,
                                      DateOnly? TimesheetThrough, DateOnly? MyTimesheetThrough, DateTimeOffset? LastImportAt);

/// <summary>What the timesheets and overrides recorded for one employee-day.</summary>
public sealed record TimesheetEntryOut(WorkMode? WorkMode, WfoSource? WfoSource, WorkMode? UploadWorkMode, int? UploadMinutes,
                                       int? BadgeMinutes, DateTimeOffset? FirstIn, DateTimeOffset? LastOut, string? UploadFilename,
                                       WorkMode? OverrideWorkMode, string? OverrideNote, string? OverriddenByName,
                                       DateTimeOffset? OverriddenAt, IReadOnlyList<string> Conflicts);

public sealed record DayDetailOut(DayRecordOut? Record, TimesheetEntryOut? Timesheet, ScheduleBrief? Shift, int LateGraceMinutes);

public sealed record DaysPage(IReadOnlyList<DayRecordOut> Items, int Total, int Page, int PageSize);

/// <summary>One calendar month of a period: office (WFO) days against the days required.</summary>
public sealed record MonthOut(string Label, DateOnly Start, DateOnly End, bool Partial, bool Covered, int WorkingDays, int OfficeDays,
                              int RequiredDays, string WfoBand, int WfhDays, int LeaveDays, int AbsentDays, double? AttendancePercentage,
                              int? AverageNetMinutes);
