using Ats.Api.Auth;
using Ats.Api.Common;
using Ats.Application.Attendance;
using Ats.Application.Audit;
using Ats.Application.Common;
using Ats.Application.Configuration;
using Ats.Application.Data;
using Ats.Application.Reports;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ats.Api.Controllers;

/// <summary>
/// Exports (<c>routes/reports.py</c> and the WFO workbook of <c>routes/wfo.py</c>): CSV reports, one employee's attendance as
/// xlsx or PDF, and the organisation's WFO workbook. Same authorisation as the dashboards; every export is audited.
/// </summary>
[ApiController]
public sealed partial class ReportsController(ReportService reports, AttendanceData data, AtsSettings settings, IAtsDb db,
                                              ILogger<ReportsController> logger) : ControllerBase
{
    private const string Csv = "text/csv; charset=utf-8";
    private const string Xlsx = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet";

    /// <summary>Builds the file, audits the export and answers it as a download (a failure is 500 report_failed, as in Python).</summary>
    private async Task<IActionResult> Export(string report, Dictionary<string, object?> parameters, Func<Task<ReportFile>> build, string mediaType,
                                             bool wrapErrors = true)
    {
        ReportFile file;
        try
        {
            file = await build().ConfigureAwait(false);
        }
        catch (Exception e) when (wrapErrors && e is not AppException && e is not OperationCanceledException)
        {
            LogFailure(logger, e, report);
            throw new AppException("The report could not be generated. Please try again.", "report_failed", 500);
        }

        AuditTrail.Record(db, HttpContext.Auth().User.Id, "REPORT_EXPORTED", "report", report, after: parameters, ctx: HttpContext.ToRequestContext());
        await db.SaveChangesAsync(HttpContext.RequestAborted).ConfigureAwait(false);
        Response.Headers.ContentDisposition = $"attachment; filename=\"{file.Filename}\"";
        Response.Headers.CacheControl = "no-store";
        return new FileContentResult(file.Content, mediaType);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "report generation failed: {Report}")]
    private static partial void LogFailure(ILogger logger, Exception exception, string report);

    private (DateOnly Start, DateOnly End, int EmployeeId) EmployeeParams()
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        var employeeId = q.RequiredInt("employee_id");
        q.ThrowIfInvalid();
        return (start, end, employeeId);
    }

    /// <summary>Daily attendance for one employee.</summary>
    [HttpGet("api/reports/employee-attendance.csv")]
    [Tags("reports")]
    [Authorize(Policy = SessionAuth.User)]
    public Task<IActionResult> EmployeeCsv(CancellationToken ct)
    {
        var (start, end, id) = EmployeeParams();
        var user = HttpContext.Auth().User;
        return Export("employee-attendance", new() { ["employee_id"] = id, ["start"] = start, ["end"] = end },
                      () => reports.EmployeeAttendanceCsvAsync(user, id, start, end, ct), Csv);
    }

    /// <summary>One employee's attendance as an Excel workbook.</summary>
    [HttpGet("api/reports/employee-attendance.xlsx")]
    [Tags("reports")]
    [Authorize(Policy = SessionAuth.User)]
    public Task<IActionResult> EmployeeXlsx(CancellationToken ct)
    {
        var (start, end, id) = EmployeeParams();
        var user = HttpContext.Auth().User;
        return Export("employee-attendance", new() { ["employee_id"] = id, ["start"] = start, ["end"] = end, ["format"] = "xlsx" }, async () =>
        {
            var report = await reports.GatherAsync(user, id, start, end, ct).ConfigureAwait(false);
            return new ReportFile(ReportService.FileName(report, "xlsx"), EmployeeWorkbook.Build(report));
        }, Xlsx);
    }

    /// <summary>One employee's attendance as a branded PDF.</summary>
    [HttpGet("api/reports/employee-attendance.pdf")]
    [Tags("reports")]
    [Authorize(Policy = SessionAuth.User)]
    public Task<IActionResult> EmployeePdfReport(CancellationToken ct)
    {
        var (start, end, id) = EmployeeParams();
        var user = HttpContext.Auth().User;
        return Export("employee-attendance", new() { ["employee_id"] = id, ["start"] = start, ["end"] = end, ["format"] = "pdf" }, async () =>
        {
            var report = await reports.GatherAsync(user, id, start, end, ct).ConfigureAwait(false);
            return new ReportFile(ReportService.FileName(report, "pdf"), EmployeePdf.Build(report));
        }, "application/pdf");
    }

    /// <summary>Per-employee summary for my scope.</summary>
    [HttpGet("api/reports/team-attendance.csv")]
    [Tags("reports")]
    [Authorize(Policy = SessionAuth.ManagerOrAdmin)]
    public Task<IActionResult> TeamCsv(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        var managerId = q.Int("manager_id");
        var departmentId = q.Int("department_id");
        q.ThrowIfInvalid();
        var user = HttpContext.Auth().User;
        return Export("team-attendance", new() { ["start"] = start, ["end"] = end, ["manager_id"] = managerId, ["department_id"] = departmentId },
                      () => reports.TeamAttendanceCsvAsync(user, start, end, managerId, departmentId, ct), Csv);
    }

    /// <summary>Monthly per-employee summary for my scope.</summary>
    [HttpGet("api/reports/monthly-attendance.csv")]
    [Tags("reports")]
    [Authorize(Policy = SessionAuth.ManagerOrAdmin)]
    public Task<IActionResult> MonthlyCsv(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var year = q.RequiredInt("year", ge: 2000, le: 2100);
        var month = q.RequiredInt("month", ge: 1, le: 12);
        var managerId = q.Int("manager_id");
        var departmentId = q.Int("department_id");
        q.ThrowIfInvalid();
        var user = HttpContext.Auth().User;
        return Export("monthly-attendance", new() { ["year"] = year, ["month"] = month, ["manager_id"] = managerId, ["department_id"] = departmentId },
                      () => reports.MonthlyAttendanceCsvAsync(user, year, month, managerId, departmentId, ct), Csv);
    }

    /// <summary>Department comparison (admin).</summary>
    [HttpGet("api/reports/department-attendance.csv")]
    [Tags("reports")]
    [Authorize(Policy = SessionAuth.Admin)]
    public Task<IActionResult> DepartmentCsv(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        q.ThrowIfInvalid();
        var user = HttpContext.Auth().User;
        return Export("department-attendance", new() { ["start"] = start, ["end"] = end },
                      () => reports.DepartmentAttendanceCsvAsync(user, start, end, ct), Csv);
    }

    /// <summary>Attendance exceptions for my scope.</summary>
    [HttpGet("api/reports/exceptions.csv")]
    [Tags("reports")]
    [Authorize(Policy = SessionAuth.ManagerOrAdmin)]
    public Task<IActionResult> ExceptionsCsv(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        var managerId = q.Int("manager_id");
        var departmentId = q.Int("department_id");
        q.ThrowIfInvalid();
        var user = HttpContext.Auth().User;
        return Export("exceptions", new() { ["start"] = start, ["end"] = end, ["manager_id"] = managerId, ["department_id"] = departmentId },
                      () => reports.ExceptionsCsvAsync(user, start, end, managerId, departmentId, ct), Csv);
    }

    /// <summary>Download the WFO report as Excel (admin).</summary>
    [HttpGet("api/org/wfo/export.xlsx")]
    [Tags("wfo")]
    [Authorize(Policy = SessionAuth.Admin)]
    public Task<IActionResult> WfoWorkbook(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var month = q.Str("month", pattern: @"^\d{4}-\d{2}$");
        var managerId = q.Int("manager_id");
        var departmentId = q.Int("department_id");
        q.ThrowIfInvalid();
        var user = HttpContext.Auth().User;
        // Python builds this one outside the report_failed wrapper: an unexpected error is a plain 500.
        return Export("wfo", new() { ["month"] = month, ["manager_id"] = managerId, ["department_id"] = departmentId },
                      () => reports.WfoWorkbookAsync(user, month, managerId, departmentId, ct), Xlsx, wrapErrors: false);
    }
}
