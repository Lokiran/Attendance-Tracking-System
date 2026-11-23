using Ats.Api.Auth;
using Ats.Api.Common;
using Ats.Application.Attendance;
using Ats.Application.Data;
using Ats.Application.Reference;
using Ats.Application.Wfo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ats.Api.Controllers;

/// <summary>
/// WFO reads (<c>routes/wfo.py</c>) and the reference lists of <c>routes/admin.py</c> (holidays, departments, policy).
/// Out-of-scope employees get 403 on the WFO routes, as the WFO API specifies.
/// </summary>
[ApiController]
public sealed class WfoReadController(WfoService wfo, ReferenceService reference, IAtsDb db) : ControllerBase
{
    private const string MonthPattern = @"^\d{4}-\d{2}$";

    /// <summary>My WFO days, weekly progress and compliance.</summary>
    [HttpGet("api/me/wfo")]
    [Tags("wfo")]
    [Authorize(Policy = SessionAuth.User)]
    public Task<EmployeeWfoOut> Mine(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var month = q.Str("month", pattern: MonthPattern);
        q.ThrowIfInvalid();
        return wfo.EmployeeMonthAsync(HttpContext.Auth().User, month, ct);
    }

    /// <summary>WFO for my team (manager: whole reporting line + me).</summary>
    [HttpGet("api/team/wfo")]
    [Tags("wfo")]
    [Authorize(Policy = SessionAuth.ManagerOrAdmin)]
    public Task<TeamWfoOut> Team(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var month = q.Str("month", pattern: MonthPattern);
        var employeeId = q.Int("employee_id");
        q.ThrowIfInvalid();
        return wfo.TeamMonthAsync(HttpContext.Auth().User, month, employeeId, ct);
    }

    /// <summary>One team member's WFO month (403 outside my team).</summary>
    [HttpGet("api/team/wfo/employees/{employeeId}")]
    [Tags("wfo")]
    [Authorize(Policy = SessionAuth.ManagerOrAdmin)]
    public async Task<EmployeeWfoOut> TeamMember(string employeeId, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("employee_id", employeeId);
        var month = q.Str("month", pattern: MonthPattern);
        q.ThrowIfInvalid();
        var employee = await wfo.ScopedEmployeeAsync(HttpContext.Auth().User, id, ct).ConfigureAwait(false);
        return await wfo.EmployeeMonthAsync(employee, month, ct).ConfigureAwait(false);
    }

    /// <summary>Organisation WFO grouped by manager (admin).</summary>
    [HttpGet("api/org/wfo")]
    [Tags("wfo")]
    [Authorize(Policy = SessionAuth.Admin)]
    public Task<OrgWfoOut> Organisation(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var month = q.Str("month", pattern: MonthPattern);
        var managerId = q.Int("manager_id");
        var departmentId = q.Int("department_id");
        q.ThrowIfInvalid();
        return wfo.OrgMonthAsync(HttpContext.Auth().User, month, managerId, departmentId, ct);
    }

    /// <summary>WFO policy (admin).</summary>
    [HttpGet("api/admin/wfo/policy")]
    [Tags("wfo")]
    [Authorize(Policy = SessionAuth.Admin)]
    public async Task<WfoPolicyOut> Policy(CancellationToken ct) =>
        WfoPolicyOut.Of(Policies.Wfo(await Policies.GetAsync(db, ct).ConfigureAwait(false)));

    /// <summary>Holidays (relevant to me unless admin).</summary>
    [HttpGet("api/holidays")]
    [Tags("administration")]
    [Authorize(Policy = SessionAuth.User)]
    public Task<List<HolidayOut>> Holidays(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var start = q.Date("start");
        var end = q.Date("end");
        q.ThrowIfInvalid();
        return reference.HolidaysAsync(HttpContext.Auth().User, start, end, ct);
    }

    [HttpGet("api/departments")]
    [Tags("administration")]
    [Authorize(Policy = SessionAuth.User)]
    public Task<List<DepartmentOut>> Departments(CancellationToken ct) => reference.DepartmentsAsync(ct);

    [HttpGet("api/policy")]
    [Tags("administration")]
    [Authorize(Policy = SessionAuth.User)]
    public Task<PolicyOut> AttendancePolicy(CancellationToken ct) => reference.PolicyAsync(ct);
}
