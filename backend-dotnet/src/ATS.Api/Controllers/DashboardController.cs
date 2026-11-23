using Ats.Api.Auth;
using Ats.Api.Common;
using Ats.Application.Attendance;
using Ats.Application.Configuration;
using Ats.Application.Dashboard;
using Ats.Application.Scope;
using Ats.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ats.Api.Controllers;

/// <summary>Team (manager) and organisation (admin) analytics (<c>routes/dashboard.py</c>); scope is enforced server-side.</summary>
[ApiController]
[Tags("dashboards")]
public sealed class DashboardController(DashboardService dashboard, ScopeService scope, AttendanceData data, AtsSettings settings) : ControllerBase
{
    private static readonly string[] SortKeys =
        ["name", "code", "department", "check_in", "net_minutes", "average_net_minutes", "attendance_percentage", "status", "exceptions"];

    private static readonly string[] Granularities = ["day", "week", "month"];

    /// <summary>Headline cards: status snapshot for a date + period statistics.</summary>
    [HttpGet("api/dashboard/overview")]
    [Authorize(Policy = SessionAuth.ManagerOrAdmin)]
    public Task<OverviewOut> Overview(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        var onDate = q.Date("on_date");
        var args = new ScopeArgs(q.Int("manager_id"), q.Int("department_id"));
        q.ThrowIfInvalid();
        return dashboard.OverviewAsync(HttpContext.Auth().User, args, start, end, onDate ?? data.Today, ct);
    }

    /// <summary>Per-employee attendance table.</summary>
    [HttpGet("api/dashboard/employees")]
    [Authorize(Policy = SessionAuth.ManagerOrAdmin)]
    public Task<EmployeeRowsPage> Employees(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        var (page, pageSize) = Params.Paging(q);
        var onDate = q.Date("on_date");
        var args = new ScopeArgs(q.Int("manager_id"), q.Int("department_id"));
        var search = q.Str("search", maxLength: 100);
        var status = q.Enum<DayStatus>("status");
        var sort = q.Choice("sort", "name", SortKeys);
        var order = q.Choice("order", "asc", "asc", "desc");
        var includeInactive = q.Bool("include_inactive", false);
        q.ThrowIfInvalid();
        return dashboard.EmployeeRowsAsync(HttpContext.Auth().User, args, start, end, onDate ?? data.Today, search, status, sort, order, page,
                                           pageSize, includeInactive, ct);
    }

    [HttpGet("api/dashboard/trends")]
    [Authorize(Policy = SessionAuth.ManagerOrAdmin)]
    public Task<TrendsOut> Trends(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        var granularity = q.Choice("granularity", "day", Granularities);
        var args = new ScopeArgs(q.Int("manager_id"), q.Int("department_id"));
        q.ThrowIfInvalid();
        return dashboard.TrendsAsync(HttpContext.Auth().User, args, start, end, granularity, ct);
    }

    [HttpGet("api/dashboard/exceptions")]
    [Authorize(Policy = SessionAuth.ManagerOrAdmin)]
    public Task<ExceptionsOut> Exceptions(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        var (page, pageSize) = Params.Paging(q);
        var args = new ScopeArgs(q.Int("manager_id"), q.Int("department_id"));
        var exceptionType = q.Str("exception_type", maxLength: 40);
        q.ThrowIfInvalid();
        return dashboard.ExceptionsAsync(HttpContext.Auth().User, args, start, end, exceptionType, page, pageSize, ct);
    }

    [HttpGet("api/dashboard/departments")]
    [Authorize(Policy = SessionAuth.Admin)]
    public Task<List<DepartmentStats>> Departments(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        var onDate = q.Date("on_date");
        q.ThrowIfInvalid();
        return dashboard.DepartmentComparisonAsync(HttpContext.Auth().User, start, end, onDate ?? data.Today, ct);
    }

    [HttpGet("api/dashboard/managers")]
    [Authorize(Policy = SessionAuth.Admin)]
    public Task<List<ManagerStats>> Managers(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (start, end) = Params.Range(q, data.Today, settings);
        var onDate = q.Date("on_date");
        var departmentId = q.Int("department_id");
        q.ThrowIfInvalid();
        return dashboard.ManagerComparisonAsync(HttpContext.Auth().User, start, end, onDate ?? data.Today, departmentId, ct);
    }

    [HttpGet("api/org/hierarchy")]
    [Authorize(Policy = SessionAuth.Admin)]
    public Task<HierarchyOut> Hierarchy(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var includeInactive = q.Bool("include_inactive", false);
        q.ThrowIfInvalid();
        return dashboard.HierarchyAsync(includeInactive, ct);
    }

    /// <summary>People in my reporting scope (includes people not tracked for attendance).</summary>
    [HttpGet("api/team/members")]
    [Authorize(Policy = SessionAuth.ManagerOrAdmin)]
    public async Task<List<TeamMember>> TeamMembers(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var managerId = q.Int("manager_id");
        var departmentId = q.Int("department_id");
        var includeInactive = q.Bool("include_inactive", false);
        var search = q.Str("search", maxLength: 100);
        var limit = q.Int("limit", ge: 1, le: 200);
        q.ThrowIfInvalid();
        var people = await scope.ResolveScopeAsync(HttpContext.Auth().User, managerId, departmentId, includeInactive, trackedOnly: false,
                                                   search: search, limit: limit, ct: ct).ConfigureAwait(false);
        return [.. people.Select(DashboardService.Member)];
    }

    /// <summary>One person in my reporting scope (404 for missing and out-of-scope alike).</summary>
    [HttpGet("api/team/members/{employeeId}")]
    [Authorize(Policy = SessionAuth.ManagerOrAdmin)]
    public async Task<TeamMember> TeamMember(string employeeId, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("employee_id", employeeId);
        q.ThrowIfInvalid();
        return DashboardService.Member(await scope.GetViewableEmployeeAsync(HttpContext.Auth().User, id, ct).ConfigureAwait(false));
    }
}
