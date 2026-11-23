using System.Globalization;
using Ats.Application.Attendance;
using Ats.Application.Wfo;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Domain.Rules;

namespace Ats.Application.Reports;

public sealed record ReportBrand(string Company, string Color, string Accent, string? Logo);

/// <summary>One calendar month of the report (<c>MonthRow</c>).</summary>
public sealed record ReportMonth(MonthOut Figures, int NoTimesheetDays)
{
    public string Status => Figures.Covered ? EmployeeReport.BandLabel[Figures.WfoBand] : "No timesheet";

    public string Color => Figures.Covered ? EmployeeReport.BandColor[Figures.WfoBand] : "94A3B8";

    public string Soft => Figures.Covered ? EmployeeReport.BandSoft[Figures.WfoBand] : "F1F5F9";

    public string Label => Figures.Label + (Figures.Partial ? " (part)" : "");
}

/// <summary>
/// One employee's attendance for the xlsx and PDF exports (<c>attendance_export.Report</c>): the same data as the dashboard
/// (day records, the summary and the WFO policy figures) and the same access rule as the CSV export.
/// </summary>
public sealed record EmployeeReport(User Employee, DateOnly Start, DateOnly End, DateTimeOffset GeneratedAt, IReadOnlyList<DayRecord> Records,
                                    Summary Summary, WfoRangeSummary Wfo, IReadOnlyList<ReportMonth> Months, ReportBrand Brand, TimeZoneInfo Zone)
{
    public static readonly IReadOnlyDictionary<DayStatus, string> StatusLabel = new Dictionary<DayStatus, string>
    {
        [DayStatus.Present] = "In office", [DayStatus.Wfh] = "Work from home", [DayStatus.Absent] = "Absent", [DayStatus.Leave] = "Leave",
        [DayStatus.Holiday] = "Holiday", [DayStatus.Weekend] = "Weekly off", [DayStatus.NoTimesheet] = "No timesheet",
        [DayStatus.NotScheduled] = "Not scheduled",
    };

    /// <summary>The dashboard's status colours (always printed next to the label, never colour alone).</summary>
    public static readonly IReadOnlyDictionary<DayStatus, string> StatusColor = new Dictionary<DayStatus, string>
    {
        [DayStatus.Present] = "0CA30C", [DayStatus.Wfh] = "0891B2", [DayStatus.Absent] = "D03B3B", [DayStatus.Leave] = "B45309",
        [DayStatus.Holiday] = "6250D6", [DayStatus.Weekend] = "94A3B8", [DayStatus.NoTimesheet] = "94A3B8", [DayStatus.NotScheduled] = "94A3B8",
    };

    public static readonly IReadOnlyDictionary<string, string> BandLabel = new Dictionary<string, string>
    {
        ["MET"] = "On target", ["NEAR"] = "Close", ["BELOW"] = "Below target",
    };

    public static readonly IReadOnlyDictionary<string, string> BandColor = new Dictionary<string, string>
    {
        ["MET"] = "0CA30C", ["NEAR"] = "EDA100", ["BELOW"] = "D03B3B",
    };

    public static readonly IReadOnlyDictionary<string, string> BandSoft = new Dictionary<string, string>
    {
        ["MET"] = "E3F4E3", ["NEAR"] = "FCF1D6", ["BELOW"] = "FBE4E4",
    };

    public int OfficeDays => Wfo.WfoDays;

    /// <summary>Office days asked for in the period: months without a timesheet for this person ask for nothing.</summary>
    public int RequiredDays => Months.Count == 0 ? Wfo.RequiredDays : Months.Where(m => m.Figures.Covered).Sum(m => m.Figures.RequiredDays);

    public string Band => WfoRules.WfoBand(OfficeDays, RequiredDays);

    public string TargetNote
    {
        get
        {
            var covered = Months.Where(m => m.Figures.Covered).ToList();
            if (covered.Count > 1)
            {
                var met = covered.Count(m => m.Figures.WfoBand == "MET");
                return met == covered.Count ? $"On target in all {met} months" : $"{BandLabel[Band]} · on target in {met} of {covered.Count} months";
            }

            return BandLabel[Band];
        }
    }

    public static string Hm(int? minutes) =>
        minutes is { } m ? string.Create(CultureInfo.InvariantCulture, $"{(int)Math.Floor(m / 60.0)}h {((m % 60) + 60) % 60:00}m") : "—";

    public string Clock(DateTimeOffset? value) =>
        value is { } v ? TimeZoneInfo.ConvertTime(v, Zone).ToString("HH:mm", CultureInfo.InvariantCulture) : "";

    public static string Pct(double? value) =>
        value is { } v ? v.ToString("F1", CultureInfo.InvariantCulture) + "%" : "—";

    public static string Period(DateOnly start, DateOnly end) =>
        $"{start.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)} – {end.ToString("dd MMM yyyy", CultureInfo.InvariantCulture)}";

    public string Generated => TimeZoneInfo.ConvertTime(GeneratedAt, Zone).ToString("dd MMM yyyy, HH:mm", CultureInfo.InvariantCulture);

    public IReadOnlyList<(string Label, string Value)> Facts =>
    [
        ("Employee", Employee.FullName), ("Employee ID", Employee.EmployeeCode), ("Department", Employee.Department?.Name ?? "—"),
        ("Reports to", Employee.Manager?.FullName ?? "—"), ("Period", Period(Start, End)), ("Generated", Generated),
    ];

    public static string BandRule(int required) =>
        $"On target: {required} or more office days · Close: up to {WfoRules.NearDays} short · Below target: more than {WfoRules.NearDays} short";

    /// <summary>The notes column: the holiday name, then the exceptions as words ("Short hours"), ABSENT left out.</summary>
    public static string Notes(DayRecord r)
    {
        ArgumentNullException.ThrowIfNull(r);
        var parts = new List<string> { r.HolidayName ?? "" };
        parts.AddRange(r.Exceptions.Where(e => e != "ABSENT").Select(e => Capitalize(e.Replace('_', ' '))));
        return string.Join("; ", parts.Where(x => x.Length > 0));
    }

    /// <summary>Python's <c>str.capitalize()</c>: the first letter upper case, the rest lower case.</summary>
    private static string Capitalize(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..].ToLowerInvariant();
}
