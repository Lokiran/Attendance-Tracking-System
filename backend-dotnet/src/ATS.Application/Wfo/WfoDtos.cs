using Ats.Domain;
using Ats.Domain.Rules;

namespace Ats.Application.Wfo;

// Shapes of backend/app/schemas/wfo.py (field order as declared; snake_case on the wire).

public sealed record WfoPolicyOut(int MinDaysPerWeek, int MinDaysPerMonth, int MinOfficeMinutes)
{
    public static WfoPolicyOut Of(WfoPolicy p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new(p.MinDaysPerWeek, p.MinDaysPerMonth, p.MinOfficeMinutes);
    }
}

/// <summary>Status: MET | SHORT | IN_PROGRESS | UPCOMING.</summary>
public sealed record WfoWeek(DateOnly WeekStart, DateOnly WeekEnd, string Label, int WorkingDays, int WfoDays, int Required,
                             string Status, int Shortfall)
{
    public static List<WfoWeek> Of(WfoSummary s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return [.. s.Weeks.Select(w => new WfoWeek(w.WeekStart, w.WeekEnd, w.Label, w.WorkingDays, w.WfoDays, w.Required, w.Status, w.Shortfall))];
    }
}

/// <summary>Compliance: COMPLIANT | NON_COMPLIANT. Band: MET | NEAR | BELOW (green / orange / red in the UI).</summary>
public sealed record WfoSummaryOut(int WorkingDays, int StandardDays, int WfoDays, int WfhDays, int OfficeDaysBelowMinimum,
                                   int LeaveDays, int HolidayDays, int WeekoffDays, int RequiredDays, int ShortfallDays,
                                   double? WfoPercentage, string Compliance, string WfoBand)
{
    public static WfoSummaryOut Of(WfoSummary s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return new(s.WorkingDays, s.StandardDays, s.WfoDays, s.WfhDays, s.OfficeDaysBelowMinimum, s.LeaveDays, s.HolidayDays,
                   s.WeekoffDays, s.RequiredDays, s.ShortfallDays, s.WfoPercentage, s.Compliance, WfoRules.WfoBand(s.WfoDays, s.RequiredDays));
    }
}

/// <summary>WfoSummaryOut plus the weekly breakdown (added to the attendance summary).</summary>
public sealed record WfoRangeSummary(int WorkingDays, int StandardDays, int WfoDays, int WfhDays, int OfficeDaysBelowMinimum,
                                     int LeaveDays, int HolidayDays, int WeekoffDays, int RequiredDays, int ShortfallDays,
                                     double? WfoPercentage, string Compliance, string WfoBand, IReadOnlyList<WfoWeek> WeeklyWfoBreakdown)
{
    public static WfoRangeSummary Of(WfoSummary s)
    {
        var o = WfoSummaryOut.Of(s);
        return new(o.WorkingDays, o.StandardDays, o.WfoDays, o.WfhDays, o.OfficeDaysBelowMinimum, o.LeaveDays, o.HolidayDays,
                   o.WeekoffDays, o.RequiredDays, o.ShortfallDays, o.WfoPercentage, o.Compliance, o.WfoBand, WfoWeek.Of(s));
    }
}

public sealed record EmployeeBrief(int Id, string EmployeeCode, string FullName, string? DepartmentName, int? ManagerId, string? ManagerName);

public sealed record WfoDay(DateOnly Date, WorkMode? WorkMode, WfoSource? WfoSource, bool IsWfoDay, int OfficeMinutes, int WfhMinutes,
                            IReadOnlyList<string> Conflicts, bool Scheduled, bool Overridden, string? OverrideNote, bool InProgress,
                            bool Future);

public sealed record EmployeeWfoOut(EmployeeBrief Employee, string Month, DateOnly Start, DateOnly End, DateOnly Today, WfoPolicyOut Policy,
                                    WfoSummaryOut Summary, IReadOnlyList<WfoWeek> WeeklyWfoBreakdown, IReadOnlyList<WfoDay> Days);

/// <summary>WfoSummaryOut fields, then the employee (Pydantic subclass field order).</summary>
public sealed record WfoRow(int WorkingDays, int StandardDays, int WfoDays, int WfhDays, int OfficeDaysBelowMinimum, int LeaveDays,
                            int HolidayDays, int WeekoffDays, int RequiredDays, int ShortfallDays, double? WfoPercentage,
                            string Compliance, string WfoBand, EmployeeBrief Employee)
{
    public static WfoRow Of(WfoSummaryOut o, EmployeeBrief employee)
    {
        ArgumentNullException.ThrowIfNull(o);
        return new(o.WorkingDays, o.StandardDays, o.WfoDays, o.WfhDays, o.OfficeDaysBelowMinimum, o.LeaveDays, o.HolidayDays,
                   o.WeekoffDays, o.RequiredDays, o.ShortfallDays, o.WfoPercentage, o.Compliance, o.WfoBand, employee);
    }
}

public sealed record WfoTotals(int Headcount, int WorkingDays, int WfoDays, int WfhDays, double? WfoPercentage, int Compliant,
                               int NonCompliant, int BandMet, int BandNear, int BandBelow);

public sealed record TeamWfoOut(string Month, DateOnly Start, DateOnly End, WfoPolicyOut Policy, WfoTotals Totals, IReadOnlyList<WfoRow> Rows);

public sealed record WfoManagerGroup(int? ManagerId, string ManagerName, string? DepartmentName, WfoTotals Totals, IReadOnlyList<WfoRow> Rows);

public sealed record OrgWfoOut(string Month, DateOnly Start, DateOnly End, WfoPolicyOut Policy, WfoTotals Totals, IReadOnlyList<WfoManagerGroup> Groups);
