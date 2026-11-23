using System.Text.Json;
using Ats.Api.Auth;
using Ats.Api.Common;
using Ats.Application.Admin;
using Ats.Application.Auth;
using Ats.Application.Reference;
using Ats.Domain;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Ats.Api.Controllers;

/// <summary>
/// Administration (<c>routes/admin.py</c>): users, managers, departments, schedules, holidays, the attendance policy and the
/// audit log. Admin only. Bodies are read field by field as the Pydantic models declare them (<see cref="BodyReader"/>).
/// </summary>
[ApiController]
[Tags("administration")]
[Authorize(Policy = SessionAuth.Admin)]
public sealed class AdminController(AdminUserService users, ConfigurationService configuration) : ControllerBase
{
    private const string CodePattern = "^[A-Za-z0-9](?:[A-Za-z0-9 ._-]*[A-Za-z0-9])?$";

    internal static void Put<T>(Dictionary<string, object?> map, string key, Sent<T> field)
    {
        if (field.Present)
        {
            map[key] = field.Value;
        }
    }

    // ------------------------------------------------------------------ users

    [HttpGet("api/admin/users")]
    public Task<PagedResult<UserAdminOut>> ListUsers(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (page, pageSize) = Params.Paging(q);
        var search = q.Str("search", maxLength: 100);
        var role = q.Enum<Role>("role");
        var departmentId = q.Int("department_id");
        var managerId = q.Int("manager_id");
        var isActive = q.BoolOrNull("is_active");
        q.ThrowIfInvalid();
        return users.ListUsersAsync(search, role, departmentId, managerId, isActive, page, pageSize, ct);
    }

    [HttpGet("api/admin/users/{userId}")]
    public async Task<UserAdminOut> GetUser(string userId, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("user_id", userId);
        q.ThrowIfInvalid();
        return UserAdminOut.Of(await users.LoadAsync(id, ct).ConfigureAwait(false));
    }

    [HttpPost("api/admin/users")]
    public async Task<IActionResult> CreateUser([FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var b = new BodyReader(q, body);
        var data = new Dictionary<string, object?>
        {
            ["employee_code"] = b.Str("employee_code", required: true, nullable: false, minLength: 2, maxLength: 32, pattern: CodePattern).Value,
            ["full_name"] = b.Str("full_name", required: true, nullable: false, minLength: 2, maxLength: 120).Value,
            ["email"] = b.Email("email", required: true, nullable: false).Value,
            ["password"] = b.Str("password", required: true, nullable: false, minLength: 1, maxLength: 128).Value,
            ["role"] = b.Enum<Role>("role", nullable: false) is { Present: true } r ? r.Value : Role.Employee,
            ["job_title"] = b.Str("job_title", maxLength: 120).Value,
            ["department_id"] = b.Int("department_id").Value,
            ["manager_id"] = b.Int("manager_id").Value,
            ["date_joined"] = b.Date("date_joined").Value,
            ["attendance_tracked"] = b.Bool("attendance_tracked", nullable: false) is { Present: true } t ? t.Value : true,
        };
        q.ThrowIfInvalid();
        var created = await users.CreateUserAsync(HttpContext.Auth().User, data, HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPatch("api/admin/users/{userId}")]
    public Task<UserAdminOut> UpdateUser(string userId, [FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("user_id", userId);
        var b = new BodyReader(q, body);
        var changes = new Dictionary<string, object?>();
        Put(changes, "employee_code", b.Str("employee_code", minLength: 2, maxLength: 32, pattern: CodePattern));
        Put(changes, "full_name", b.Str("full_name", minLength: 2, maxLength: 120));
        Put(changes, "email", b.Email("email"));
        Put(changes, "role", b.Enum<Role>("role"));
        Put(changes, "job_title", b.Str("job_title", maxLength: 120));
        Put(changes, "department_id", b.Int("department_id"));
        Put(changes, "manager_id", b.Int("manager_id"));
        Put(changes, "is_active", b.Bool("is_active"));
        Put(changes, "attendance_tracked", b.Bool("attendance_tracked"));
        Put(changes, "date_joined", b.Date("date_joined"));
        q.ThrowIfInvalid();
        return users.UpdateUserAsync(HttpContext.Auth().User, id, changes, HttpContext.ToRequestContext(), ct);
    }

    [HttpPost("api/admin/users/{userId}/reset-password")]
    public async Task<MessageResponse> ResetPassword(string userId, [FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("user_id", userId);
        var password = new BodyReader(q, body).Str("password", required: true, nullable: false, minLength: 1, maxLength: 128).Value;
        q.ThrowIfInvalid();
        await users.ResetPasswordAsync(HttpContext.Auth().User, id, password!, HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
        return new MessageResponse("Password reset; the user's sessions were signed out");
    }

    /// <summary>Active users who can be assigned as managers.</summary>
    [HttpGet("api/managers")]
    public Task<List<UserAdminOut>> Managers(CancellationToken ct) => users.ManagersAsync(ct);

    // ------------------------------------------------------------------ departments

    [HttpPost("api/admin/departments")]
    public async Task<IActionResult> CreateDepartment([FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var b = new BodyReader(q, body);
        var name = b.Str("name", required: true, nullable: false, minLength: 2, maxLength: 100).Value;
        var description = b.Str("description", maxLength: 2000).Value;
        var head = b.Int("department_head_id").Value;
        q.ThrowIfInvalid();
        var created = await users.CreateDepartmentAsync(HttpContext.Auth().User, name!, description, head, HttpContext.ToRequestContext(), ct)
            .ConfigureAwait(false);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPatch("api/admin/departments/{deptId}")]
    public Task<DepartmentOut> UpdateDepartment(string deptId, [FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("dept_id", deptId);
        var b = new BodyReader(q, body);
        var changes = new Dictionary<string, object?>();
        Put(changes, "name", b.Str("name", minLength: 2, maxLength: 100));
        Put(changes, "description", b.Str("description", maxLength: 2000));
        Put(changes, "department_head_id", b.Int("department_head_id"));
        q.ThrowIfInvalid();
        return users.UpdateDepartmentAsync(HttpContext.Auth().User, id, changes, HttpContext.ToRequestContext(), ct);
    }

    [HttpDelete("api/admin/departments/{deptId}")]
    public async Task<MessageResponse> DeleteDepartment(string deptId, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("dept_id", deptId);
        q.ThrowIfInvalid();
        await users.DeleteDepartmentAsync(HttpContext.Auth().User, id, HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
        return new MessageResponse("Department deleted");
    }

    // ------------------------------------------------------------------ schedules

    [HttpGet("api/admin/schedules")]
    public Task<List<ScheduleOut>> Schedules(CancellationToken ct) => configuration.SchedulesAsync(ct);

    [HttpPost("api/admin/schedules")]
    public async Task<IActionResult> CreateSchedule([FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var b = new BodyReader(q, body);
        var name = b.Str("name", required: true, nullable: false, minLength: 2, maxLength: 100).Value;
        var employeeId = b.Int("employee_id").Value;
        var departmentId = b.Int("department_id").Value;
        var start = b.Time("start_time", required: true, nullable: false).Value;
        var end = b.Time("end_time", required: true, nullable: false).Value;
        var minutes = b.Int("required_minutes", required: true, nullable: false, ge: 1, le: 1440).Value;
        var days = b.IntList("working_days", required: true, nullable: false, minItems: 1, maxItems: 7).Value;
        var from = b.Date("effective_from", required: true, nullable: false).Value;
        var to = b.Date("effective_to").Value;
        q.ThrowIfInvalid();
        var data = new ConfigurationService.ScheduleData(name!, employeeId, departmentId, start!.Value, end!.Value, minutes!.Value, days!, from!.Value, to);
        var created = await configuration.CreateScheduleAsync(HttpContext.Auth().User, data, HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPatch("api/admin/schedules/{scheduleId}")]
    public Task<ScheduleOut> UpdateSchedule(string scheduleId, [FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("schedule_id", scheduleId);
        var b = new BodyReader(q, body);
        var changes = new Dictionary<string, object?>();
        Put(changes, "name", b.Str("name", minLength: 2, maxLength: 100));
        Put(changes, "start_time", b.Time("start_time"));
        Put(changes, "end_time", b.Time("end_time"));
        Put(changes, "required_minutes", b.Int("required_minutes", ge: 1, le: 1440));
        Put(changes, "working_days", b.IntList("working_days", minItems: 1, maxItems: 7));
        Put(changes, "effective_from", b.Date("effective_from"));
        Put(changes, "effective_to", b.Date("effective_to"));
        q.ThrowIfInvalid();
        return configuration.UpdateScheduleAsync(HttpContext.Auth().User, id, changes, HttpContext.ToRequestContext(), ct);
    }

    [HttpDelete("api/admin/schedules/{scheduleId}")]
    public async Task<MessageResponse> DeleteSchedule(string scheduleId, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("schedule_id", scheduleId);
        q.ThrowIfInvalid();
        await configuration.DeleteScheduleAsync(HttpContext.Auth().User, id, HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
        return new MessageResponse("Schedule deleted");
    }

    // ------------------------------------------------------------------ holidays

    [HttpPost("api/admin/holidays")]
    public async Task<IActionResult> CreateHoliday([FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var b = new BodyReader(q, body);
        var name = b.Str("name", required: true, nullable: false, minLength: 2, maxLength: 120).Value;
        var date = b.Date("holiday_date", required: true, nullable: false).Value;
        var departmentId = b.Int("department_id").Value;
        q.ThrowIfInvalid();
        var created = await configuration.CreateHolidayAsync(HttpContext.Auth().User, name!, date!.Value, departmentId, HttpContext.ToRequestContext(), ct)
            .ConfigureAwait(false);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpDelete("api/admin/holidays/{holidayId}")]
    public async Task<MessageResponse> DeleteHoliday(string holidayId, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var id = q.PathInt("holiday_id", holidayId);
        q.ThrowIfInvalid();
        await configuration.DeleteHolidayAsync(HttpContext.Auth().User, id, HttpContext.ToRequestContext(), ct).ConfigureAwait(false);
        return new MessageResponse("Holiday deleted");
    }

    // ------------------------------------------------------------------ policy and audit log

    [HttpPut("api/admin/policy")]
    public Task<PolicyOut> UpdatePolicy([FromBody] JsonElement body, CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var b = new BodyReader(q, body);
        var data = new Dictionary<string, object?>();
        Put(data, "min_present_minutes", b.Int("min_present_minutes", ge: 0, le: 1440));
        Put(data, "count_incomplete_as_present", b.Bool("count_incomplete_as_present"));
        Put(data, "max_session_minutes", b.Int("max_session_minutes", ge: 60, le: 1440));
        Put(data, "late_grace_minutes", b.Int("late_grace_minutes", ge: 0, le: 240));
        Put(data, "correction_window_days", b.Int("correction_window_days", ge: 1, le: 365));
        Put(data, "manager_can_approve", b.Bool("manager_can_approve"));
        Put(data, "notes", b.Str("notes", maxLength: 2000));
        q.ThrowIfInvalid();
        return configuration.UpdatePolicyAsync(HttpContext.Auth().User, data, HttpContext.ToRequestContext(), ct);
    }

    [HttpGet("api/admin/audit-logs")]
    public Task<PagedResult<AuditOut>> AuditLogs(CancellationToken ct)
    {
        var q = new QueryReader(Request);
        var (page, pageSize) = Params.Paging(q);
        var action = q.Str("action", maxLength: 64);
        var entityType = q.Str("entity_type", maxLength: 64);
        var actorId = q.Int("actor_id");
        var search = q.Str("search", maxLength: 100);
        var start = q.Date("start");
        var end = q.Date("end");
        q.ThrowIfInvalid();
        return configuration.AuditLogsAsync(action, entityType, actorId, search, start, end, page, pageSize, ct);
    }
}
