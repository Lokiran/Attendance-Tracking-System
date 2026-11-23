using Ats.Api.Auth;
using Ats.Api.Common;
using Ats.Application.Attendance;
using Ats.Application.Configuration;
using Ats.Application.Dashboard;
using Ats.Application.Scope;
using Ats.Application.Wfo;
using Ats.Domain;
using Ats.Domain.Rules;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ats.Api.Controllers;

/// <summary>
/// Attendance reads (<c>backend/app/api/routes/attendance.py</c>). Attendance comes from imported timesheets. Every
/// employee route answers 404 "Employee not found" for missing and out-of-scope employees alike.
/// </summary>
[ApiController]
[Route("api/attendance")]
[Tags("attendance")]
[Authorize(Policy = SessionAuth.User)]
public sealed class AttendanceController(AttendanceService attendance, WfoService wfo, ScopeService scope, AttendanceData data,
                                         AtsSettings settings) : ControllerBase
{
    /// <summary>Today, my shift and how far timesheets reach.</summary>
    [HttpGet("current")]
    public Task<CurrentStatusOut> Current(CancellationToken ct) => attendance.CurrentStatusAsync(HttpContext.Auth().User, ct);

    [HttpGet("employees/{employeeId}/summary")]
    public async Task<SummaryOut> Summary(string employeeId, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        var id = q.PathInt("employee_id", employeeId);
        q.ThrowIfInvalid();
        var employee = await scope.GetViewableEmployeeAsync(HttpContext.Auth().User, id, ct).ConfigureAwait(false);
        var (summary, _) = await attendance.SummaryForAsync(employee, start, end, ct).ConfigureAwait(false);
        return SummaryOut.Of(summary, await wfo.RangeSummaryAsync(employee, start, end, ct).ConfigureAwait(false));
    }

    /// <summary>Day / week / month buckets for one employee.</summary>
    [HttpGet("employees/{employeeId}/trends")]
    public async Task<TrendsOut> Trends(string employeeId, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        var id = q.PathInt("employee_id", employeeId);
        var granularity = q.Choice("granularity", "month", "day", "week", "month");
        q.ThrowIfInvalid();
        var employee = await scope.GetViewableEmployeeAsync(HttpContext.Auth().User, id, ct).ConfigureAwait(false);
        var records = await attendance.RecordsForAsync(employee, start, end, ct).ConfigureAwait(false);
        return new TrendsOut(granularity, 1, DashboardService.Bucketize(records, end, granularity)); // TODO(parity) P10: always 1
    }

    /// <summary>Office days against the requirement, month by month.</summary>
    [HttpGet("employees/{employeeId}/months")]
    public async Task<List<MonthOut>> Months(string employeeId, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        var id = q.PathInt("employee_id", employeeId);
        q.ThrowIfInvalid();
        var employee = await scope.GetViewableEmployeeAsync(HttpContext.Auth().User, id, ct).ConfigureAwait(false);
        var records = await attendance.RecordsForAsync(employee, start, end, ct).ConfigureAwait(false);
        return await attendance.MonthRowsAsync(employee, records, start, end, ct).ConfigureAwait(false);
    }

    /// <summary>Daily records (newest first).</summary>
    [HttpGet("employees/{employeeId}/days")]
    public async Task<DaysPage> Days(string employeeId, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        var (page, pageSize) = Params.Paging(q);
        var id = q.PathInt("employee_id", employeeId);
        var status = q.Enum<DayStatus>("status");
        var exceptionsOnly = q.Bool("exceptions_only", false);
        var order = q.Str("order", pattern: "^(asc|desc)$") ?? "desc";
        q.ThrowIfInvalid();
        var employee = await scope.GetViewableEmployeeAsync(HttpContext.Auth().User, id, ct).ConfigureAwait(false);
        IEnumerable<DayRecord> records = await attendance.RecordsForAsync(employee, start, end, ct).ConfigureAwait(false);
        if (status is { } wanted)
        {
            records = records.Where(r => r.Status == wanted);
        }

        if (exceptionsOnly)
        {
            records = records.Where(r => r.Exceptions.Count > 0);
        }

        var list = order == "desc" ? records.Reverse().ToList() : records.ToList();
        return new DaysPage([.. list.Skip((page - 1) * pageSize).Take(pageSize).Select(DayRecordOut.Of)], list.Count, page, pageSize);
    }

    /// <summary>One day's record and the timesheet entry behind it.</summary>
    [HttpGet("employees/{employeeId}/days/{day}")]
    public async Task<DayDetailOut> Day(string employeeId, string day, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("employee_id", employeeId);
        var date = q.PathDate("day", day);
        q.ThrowIfInvalid();
        var employee = await scope.GetViewableEmployeeAsync(HttpContext.Auth().User, id, ct).ConfigureAwait(false);
        return await attendance.DayDetailAsync(employee, date, ct).ConfigureAwait(false);
    }
}
