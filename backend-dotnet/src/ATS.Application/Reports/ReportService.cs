using System.Globalization;
using System.Text.RegularExpressions;
using Ats.Application.Attendance;
using Ats.Application.Common;
using Ats.Application.Configuration;
using Ats.Application.Dashboard;
using Ats.Application.Data;
using Ats.Application.Scope;
using Ats.Application.Wfo;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Domain.Rules;
using ClosedXML.Excel;
using Microsoft.Extensions.Logging;

namespace Ats.Application.Reports;

/// <summary>A generated file: its download name and content.</summary>
public sealed record ReportFile(string Filename, byte[] Content);

/// <summary>
/// The exports (<c>app/services/reports.py</c>, <c>attendance_export.py</c>, <c>wfo_export.py</c>): CSV reports with the
/// dashboards' scope rules, one employee's attendance as xlsx or PDF, and the organisation's WFO workbook.
/// </summary>
public sealed partial class ReportService(IAtsDb db, ScopeService scope, AttendanceData data, AttendanceService attendance, WfoService wfo,
                                          DashboardService dashboard, AtsSettings settings, IClock clock, ILogger<ReportService> logger)
{
    private static readonly string[] EmployeeHeader =
        ["Employee ID", "Name", "Date", "Status", "In", "Out", "Office hours (blank: not in timesheet)", "Required hours", "Exceptions", "Holiday"];

    private static readonly string[] SummaryHeader =
        ["Employee ID", "Name", "Department", "Manager", "Expected days", "Present (expected) days", "Extra days worked", "Absent days",
         "WFH days", "Leave days", "Days without timesheet", "Late days", "Days with hours", "Office hours", "Required hours (days with hours)",
         "Variance hours", "Avg office hours / day", "Attendance %"];

    private static readonly string[] DepartmentHeader =
        ["Department", "Head", "Headcount", "Expected days", "Present (expected) days", "Absent days", "WFH days", "Office hours",
         "Required hours (days with hours)", "Avg office hours / day", "Attendance %"];

    private static readonly string[] ExceptionsHeader =
        ["Date", "Employee ID", "Name", "Department", "Status", "Exceptions", "In", "Out", "Office hours", "Required hours"];

    private TimeZoneInfo Zone => settings.TimeZone;

    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------------ CSV

    public async Task<ReportFile> EmployeeAttendanceCsvAsync(User viewer, int employeeId, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        var employee = await scope.GetViewableEmployeeAsync(viewer, employeeId, ct).ConfigureAwait(false);
        var records = await attendance.RecordsForAsync(employee, start, end, ct).ConfigureAwait(false);
        var rows = records.Select(r => (IReadOnlyList<object?>)
        [
            employee.EmployeeCode, employee.FullName, r.Date, EnumText.Of(r.Status), r.FirstIn, r.LastOut,
            r.HoursKnown ? CsvReport.Hours(r.NetMinutes) : "", CsvReport.Hours(r.RequiredMinutes), string.Join("; ", r.Exceptions), r.HolidayName ?? "",
        ]);
        return new ReportFile($"attendance_{employee.EmployeeCode}_{Iso(start)}_{Iso(end)}.csv", CsvReport.ToCsv(EmployeeHeader, rows, Zone));
    }

    private async Task<List<IReadOnlyList<object?>>> SummaryRowsAsync(List<User> employees, DateOnly start, DateOnly end, CancellationToken ct)
    {
        var records = (await data.ComputeRecordsAsync(employees, start, end, ct).ConfigureAwait(false)).ByEmployee;
        var output = new List<IReadOnlyList<object?>>();
        foreach (var e in employees)
        {
            var s = AttendanceRules.Summarize(records[e.Id], start, end);
            output.Add(
            [
                e.EmployeeCode, e.FullName, e.Department?.Name ?? "", e.Manager?.FullName ?? "", s.ExpectedDays, s.PresentExpectedDays,
                s.ExtraPresentDays, s.AbsentDays, s.WfhDays, s.LeaveDays, s.NoTimesheetDays, s.LateDays, s.HoursDays, CsvReport.Hours(s.NetMinutes),
                CsvReport.Hours(s.HoursRequiredMinutes), CsvReport.Hours(s.VarianceMinutes), CsvReport.Hours(s.AverageNetMinutes),
                s.AttendancePercentage is { } p ? p : "",
            ]);
        }

        return output;
    }

    public async Task<ReportFile> TeamAttendanceCsvAsync(User viewer, DateOnly start, DateOnly end, int? managerId, int? departmentId,
                                                         CancellationToken ct = default)
    {
        var employees = await scope.ResolveScopeAsync(viewer, managerId, departmentId, ct: ct).ConfigureAwait(false);
        return new ReportFile($"team_attendance_{Iso(start)}_{Iso(end)}.csv",
                              CsvReport.ToCsv(SummaryHeader, await SummaryRowsAsync(employees, start, end, ct).ConfigureAwait(false), Zone));
    }

    public async Task<ReportFile> MonthlyAttendanceCsvAsync(User viewer, int year, int month, int? managerId, int? departmentId,
                                                            CancellationToken ct = default)
    {
        var start = new DateOnly(year, month, 1);
        var end = OfficeTime.MonthEnd(start);
        var employees = await scope.ResolveScopeAsync(viewer, managerId, departmentId, ct: ct).ConfigureAwait(false);
        var label = string.Create(CultureInfo.InvariantCulture, $"{year}-{month:00}");
        var rows = (await SummaryRowsAsync(employees, start, end, ct).ConfigureAwait(false)).Select(r => (IReadOnlyList<object?>)[label, .. r]);
        return new ReportFile($"monthly_attendance_{label}.csv", CsvReport.ToCsv(["Month", .. SummaryHeader], rows, Zone));
    }

    public async Task<ReportFile> DepartmentAttendanceCsvAsync(User viewer, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        var departments = await dashboard.DepartmentComparisonAsync(viewer, start, end, end, ct).ConfigureAwait(false);
        var rows = departments.Select(d => (IReadOnlyList<object?>)
        [
            d.DepartmentName, d.HeadName, d.Headcount, d.Period.ExpectedDays, d.Period.PresentExpectedDays, d.Period.AbsentDays, d.Period.WfhDays,
            CsvReport.Hours(d.Period.NetMinutes), CsvReport.Hours(d.Period.HoursRequiredMinutes), CsvReport.Hours(d.Period.AverageNetMinutes),
            d.Period.AttendancePercentage is { } p ? p : "",
        ]);
        return new ReportFile($"department_attendance_{Iso(start)}_{Iso(end)}.csv", CsvReport.ToCsv(DepartmentHeader, rows, Zone));
    }

    public async Task<ReportFile> ExceptionsCsvAsync(User viewer, DateOnly start, DateOnly end, int? managerId, int? departmentId,
                                                     CancellationToken ct = default)
    {
        var result = await dashboard.ExceptionsAsync(viewer, new ScopeArgs(managerId, departmentId), start, end, null, 1, 100_000, ct).ConfigureAwait(false);
        var rows = result.Items.Select(r => (IReadOnlyList<object?>)
        [
            r.Date, r.EmployeeCode, r.FullName, r.DepartmentName, EnumText.Of(r.Status), string.Join("; ", r.Exceptions), r.FirstIn, r.LastOut,
            r.HoursKnown ? CsvReport.Hours(r.NetMinutes) : "", CsvReport.Hours(r.RequiredMinutes),
        ]);
        return new ReportFile($"attendance_exceptions_{Iso(start)}_{Iso(end)}.csv", CsvReport.ToCsv(ExceptionsHeader, rows, Zone));
    }

    // ------------------------------------------------------------------ one employee (xlsx / PDF)

    public async Task<EmployeeReport> GatherAsync(User viewer, int employeeId, DateOnly start, DateOnly end, CancellationToken ct = default)
    {
        var employee = await scope.GetViewableEmployeeAsync(viewer, employeeId, ct).ConfigureAwait(false);
        var records = await attendance.RecordsForAsync(employee, start, end, ct).ConfigureAwait(false);
        var summary = AttendanceRules.Summarize(records, start, end);
        var range = await wfo.RangeSummaryAsync(employee, start, end, ct).ConfigureAwait(false);
        var months = await attendance.MonthFiguresAsync(employee, records, start, end, ct).ConfigureAwait(false);
        return new EmployeeReport(employee, start, end, clock.UtcNow, records, summary, range,
                                  [.. months.Select(m => new ReportMonth(m.Month, m.NoTimesheetDays))], Brand(), Zone);
    }

    /// <summary>Branding from the settings, never from the code (REPORT_COMPANY_NAME / LOGO_FILE / BRAND_COLOR / ACCENT_COLOR).</summary>
    public ReportBrand Brand()
    {
        string? logo = null;
        if (settings.ReportLogoFile.Trim() is { Length: > 0 } raw)
        {
            var path = settings.ResolvePath(raw);
            if (File.Exists(path))
            {
                logo = path;
            }
            else
            {
                LogMissingLogo(logger, path);
            }
        }

        return new ReportBrand(settings.ReportCompanyName.Trim(), HexColor(settings.ReportBrandColor, "0F172A"), HexColor(settings.ReportAccentColor, "2563EB"), logo);
    }

    private static string HexColor(string? value, string fallback)
    {
        var m = HexPattern().Match((value ?? "").Trim());
        return m.Success ? m.Groups[1].Value.ToUpperInvariant() : fallback;
    }

    public static string FileName(EmployeeReport report, string extension)
    {
        ArgumentNullException.ThrowIfNull(report);
        var code = UnsafeName().Replace(report.Employee.EmployeeCode, "_").Trim('_');
        return $"attendance_{(code.Length > 0 ? code : "employee")}_{Iso(report.Start)}_{Iso(report.End)}.{extension}";
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "REPORT_LOGO_FILE not found: {Path} (the report is generated without a logo)")]
    private static partial void LogMissingLogo(ILogger logger, string path);

    [GeneratedRegex("^#?([0-9A-Fa-f]{6})$")]
    private static partial Regex HexPattern();

    [GeneratedRegex("[^A-Za-z0-9._-]+")]
    private static partial Regex UnsafeName();

    // ------------------------------------------------------------------ WFO workbook (organisation)

    private static readonly string[] RiskyStarts = ["=", "+", "-", "@", "\t", "\r"];

    /// <summary>Text cells a spreadsheet could run as a formula get a leading apostrophe (CWE-1236).</summary>
    internal static object? SafeText(object? value) =>
        value is string s && RiskyStarts.Any(r => s.StartsWith(r, StringComparison.Ordinal)) ? "'" + s : value;

    internal static void SetValue(IXLCell cell, object? value)
    {
        switch (value)
        {
            case null:
                break;
            case string s:
                // ClosedXML reads a leading apostrophe as Excel's quote prefix and drops it; doubling it stores the text as
                // written, as openpyxl does (so an escaped "'=..." keeps its apostrophe).
                cell.Value = s.StartsWith('\'') ? "'" + s : s;
                break;
            case int i:
                cell.Value = i;
                break;
            case double d:
                cell.Value = d;
                break;
            case bool b:
                cell.Value = b;
                break;
            case DateOnly d:
                cell.Value = d.ToDateTime(TimeOnly.MinValue);
                cell.Style.DateFormat.Format = "yyyy-mm-dd";
                break;
            default:
                cell.Value = Convert.ToString(value, CultureInfo.InvariantCulture);
                break;
        }
    }

    private static readonly XLColor HeaderFill = XLColor.FromHtml("#E8EDF5"); // the app's secondary-soft table header tint

    private static IXLWorksheet Sheet(XLWorkbook wb, string title, string[] header, List<object?[]> rows)
    {
        var ws = wb.Worksheets.Add(title);
        for (var c = 0; c < header.Length; c++)
        {
            var cell = ws.Cell(1, c + 1);
            cell.Value = header[c];
            cell.Style.Font.Bold = true;
            cell.Style.Fill.BackgroundColor = HeaderFill;
        }

        for (var r = 0; r < rows.Count; r++)
        {
            for (var c = 0; c < rows[r].Length; c++)
            {
                SetValue(ws.Cell(r + 2, c + 1), SafeText(rows[r][c]));
            }
        }

        for (var c = 0; c < header.Length; c++)
        {
            var width = Math.Max(header[c].Length, rows.Take(500).Where(r => r[c] is not null).Select(r => Text(r[c]).Length).DefaultIfEmpty(0).Max());
            ws.Column(c + 1).Width = Math.Min(48, width + 2);
        }

        ws.SheetView.FreezeRows(1);
        return ws;
    }

    /// <summary><c>str(value)</c> for the column widths.</summary>
    private static string Text(object? v) => v switch
    {
        DateOnly d => Iso(d),
        double d => Uploads.PyText.Str(d),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture) ?? "",
    };

    public async Task<ReportFile> WfoWorkbookAsync(User admin, string? month, int? managerId, int? departmentId, CancellationToken ct = default)
    {
        var today = data.Today;
        var (label, start, end) = WfoService.MonthBounds(month, today);
        var policy = Policies.Wfo(await Policies.GetAsync(db, ct).ConfigureAwait(false));
        var people = await scope.ResolveScopeAsync(admin, managerId, departmentId, ct: ct).ConfigureAwait(false);
        var summaryRows = new List<object?[]>();
        var dailyRows = new List<object?[]>();
        for (var i = 0; i < people.Count; i += 200)
        {
            var chunk = people.Skip(i).Take(200).ToList();
            var classified = await wfo.ClassifyRangeAsync(chunk, start.AddDays(-WfoService.WeekPad), end.AddDays(WfoService.WeekPad), ct: ct)
                .ConfigureAwait(false);
            foreach (var e in chunk)
            {
                var ed = classified[e.Id];
                var s = WfoService.Summarize(ed, start, end, today, policy);
                var band = WfoRules.WfoBand(s.WfoDays, s.RequiredDays) switch { "MET" => "Met", "NEAR" => "Close (up to 3 days short)", _ => "Below" };
                summaryRows.Add(
                [
                    e.EmployeeCode, e.FullName, e.Department?.Name ?? "", e.Manager?.FullName ?? "", s.WorkingDays, s.WfoDays, s.WfhDays, s.LeaveDays,
                    s.WfoPercentage is { } p ? p : "", s.RequiredDays, s.ShortfallDays, s.Compliance, band,
                ]);
                foreach (var c in ed.Days)
                {
                    if (c.Date >= today || c.Date < start || c.Date > end)
                    {
                        continue;
                    }

                    dailyRows.Add(
                    [
                        e.EmployeeCode, e.FullName, c.Date, c.Class.WorkMode is { } m ? EnumText.Of(m) : "", c.Class.Source is { } src ? EnumText.Of(src) : "",
                        WfoRules.IsWfoDay(WfoService.PolicyDayOf(c), policy) ? "Yes" : "No", PyMath.Round(c.Class.OfficeMinutes / 60.0, 2),
                        PyMath.Round(c.Class.WfhMinutes / 60.0, 2), string.Join("; ", c.Class.Conflicts),
                    ]);
                }
            }
        }

        using var wb = new XLWorkbook();
        Sheet(wb, "Summary", ["Employee ID", "Name", "Department", "Manager", "Working days", "WFO days", "WFH days", "Leave days", "WFO %",
                              "Required WFO days", "Shortfall days", "Compliance", "WFO status"], summaryRows);
        Sheet(wb, "Daily", ["Employee ID", "Name", "Date", "Work mode", "Source", "WFO day", "Office hours", "WFH hours", "Conflicts"], dailyRows);
        var yesterday = today.AddDays(-1);
        Sheet(wb, "Policy", ["Setting", "Value"],
        [
            ["Month", label], ["Minimum WFO days per week", policy.MinDaysPerWeek], ["Minimum WFO days per month", policy.MinDaysPerMonth],
            ["Minimum office hours for a WFO day", policy.MinOfficeMinutes / 60.0],
            ["Counted up to", start < today ? (end < yesterday ? end : yesterday) : "nothing yet"],
        ]);
        using var stream = new MemoryStream();
        wb.SaveAs(stream);
        return new ReportFile($"wfo_report_{label}.xlsx", stream.ToArray());
    }
}
