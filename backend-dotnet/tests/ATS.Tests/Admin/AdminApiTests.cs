using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Tests.Auth;
using Ats.Tests.Uploads;
using FluentAssertions;

namespace Ats.Tests.Admin;

/// <summary>
/// Administration through the API: ports of backend/app/tests/test_admin.py and test_permissions.py (the admin parts).
/// Clock: Tuesday 10 March 2026, 12:00 IST; org: admin; Vikram -> Priya, Anil; Priya -> Rahul, Sneha; Anil -> Arjun.
/// </summary>
public sealed class AdminApiTests : IDisposable
{
    private readonly AuthHost _host = new();

    public AdminApiTests() => _host.Clock.UtcNow = new DateTimeOffset(2026, 3, 10, 6, 30, 0, TimeSpan.Zero);

    public void Dispose() => _host.Dispose();

    private Org Org => _host.Org;

    private static Dictionary<string, object?> NewUser(params (string Key, object? Value)[] changes)
    {
        var body = new Dictionary<string, object?>
        {
            ["employee_code"] = "EMP900", ["full_name"] = "Test Person", ["email"] = "test.person@test.example", ["password"] = "Welcome12345",
            ["role"] = "EMPLOYEE",
        };
        foreach (var (k, v) in changes)
        {
            body[k] = v;
        }

        return body;
    }

    private Task<(HttpStatusCode Status, JsonElement Body)> Post(string path, string csrf, object body) => _host.Send(HttpMethod.Post, path, csrf, body);

    private Task<(HttpStatusCode Status, JsonElement Body)> Patch(string path, string csrf, object body) => _host.Send(HttpMethod.Patch, path, csrf, body);

    private async Task<JsonElement> Get(string path) => await WriteClient.Json(await _host.Client.GetAsync(new Uri(path, UriKind.Relative)));

    [Fact]
    public async Task Create_employee_and_audit()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        var (status, body) = await Post("/api/admin/users", csrf, NewUser(("department_id", Org.Ai.Id), ("manager_id", Org.Priya.Id)));
        status.Should().Be(HttpStatusCode.Created, body.ToString());
        body.GetProperty("manager").GetProperty("full_name").GetString().Should().Be("Priya Sharma");
        body.ToString().Should().NotContain("password");
        using var db = _host.Db();
        var log = db.AuditLogs.Single(a => a.Action == "USER_CREATED");
        log.ActorUserId.Should().Be(Org.Admin.Id);
        log.AfterValues!.ToJsonString().Should().NotContain("Welcome12345").And.NotContain("password_hash");
    }

    [Fact]
    public async Task Duplicate_email_and_code_rejected()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        (await Post("/api/admin/users", csrf, NewUser())).Status.Should().Be(HttpStatusCode.Created);
        (await Post("/api/admin/users", csrf, NewUser(("employee_code", "EMP901")))).Status.Should().Be(HttpStatusCode.Conflict);
        (await Post("/api/admin/users", csrf, NewUser(("email", "other@test.example"), ("employee_code", "emp900")))).Status.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Employee_codes_may_have_inner_spaces_and_dots()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        var (status, body) = await Post("/api/admin/users", csrf, NewUser(("employee_code", "Intern - 10001")));
        (status, body.GetProperty("employee_code").GetString()).Should().Be((HttpStatusCode.Created, "INTERN - 10001"));
        foreach (var bad in new[] { " EMP1", "EMP1 ", "EMP#1", "-EMP", "EMP1\n" })
        {
            var (s, b) = await Post("/api/admin/users", csrf, NewUser(("employee_code", bad), ("email", "x@test.example")));
            s.Should().Be(HttpStatusCode.UnprocessableEntity, bad);
            b.GetProperty("error").GetProperty("message").GetString().Should()
                .Be("employee_code: String should match pattern '^[A-Za-z0-9](?:[A-Za-z0-9 ._-]*[A-Za-z0-9])?$'");
        }
    }

    [Fact]
    public async Task Body_problems_are_reported_in_field_order_with_pydantic_messages()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        var (status, body) = await Post("/api/admin/users", csrf, new
        {
            employee_code = "X", full_name = 5, email = "nope", role = "BOSS", department_id = "abc", attendance_tracked = "perhaps",
            date_joined = "2026-02-30",
        });
        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        var details = body.GetProperty("error").GetProperty("details").EnumerateArray()
            .Select(d => $"{d.GetProperty("field").GetString()}: {d.GetProperty("message").GetString()}").ToList();
        details.Should().Equal(
            "employee_code: String should have at least 2 characters",
            "full_name: Input should be a valid string",
            "email: value is not a valid email address: An email address must have an @-sign.",
            "password: Field required",
            "role: Input should be 'EMPLOYEE', 'MANAGER' or 'ADMIN'",
            "department_id: Input should be a valid integer, unable to parse string as an integer",
            "date_joined: Input should be a valid date or datetime, day value is outside expected range",
            "attendance_tracked: Input should be a valid boolean, unable to interpret input");
        (await Post("/api/admin/users", csrf, new[] { 1 })).Body.GetProperty("error").GetProperty("message").GetString().Should().Be("Invalid request");
    }

    [Fact]
    public async Task Manager_assignment_validation()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        (await Patch($"/api/admin/users/{Org.Priya.Id}", csrf, new { manager_id = Org.Priya.Id })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        var (status, body) = await Patch($"/api/admin/users/{Org.Vikram.Id}", csrf, new { manager_id = Org.Priya.Id });
        status.Should().Be(HttpStatusCode.UnprocessableEntity);
        body.GetProperty("error").GetProperty("message").GetString().Should().Contain("circular");
        (await Patch($"/api/admin/users/{Org.Sneha.Id}", csrf, new { manager_id = Org.Rahul.Id })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Reassign_manager_and_department()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        var (status, body) = await Patch($"/api/admin/users/{Org.Arjun.Id}", csrf, new { manager_id = Org.Priya.Id, department_id = Org.Ai.Id });
        status.Should().Be(HttpStatusCode.OK);
        (body.GetProperty("manager").GetProperty("id").GetInt32(), body.GetProperty("department").GetProperty("id").GetInt32())
            .Should().Be((Org.Priya.Id, Org.Ai.Id));
        using var db = _host.Db();
        var log = db.AuditLogs.Single(a => a.EntityId == Org.Arjun.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        log.Action.Should().Be("USER_UPDATED");
        (log.BeforeValues!["manager_id"]!.GetValue<int>(), log.AfterValues!["manager_id"]!.GetValue<int>()).Should().Be((Org.Anil.Id, Org.Priya.Id));
    }

    [Fact]
    public async Task Cannot_demote_or_deactivate_last_admin()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        (await Patch($"/api/admin/users/{Org.Admin.Id}", csrf, new { role = "EMPLOYEE" })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Patch($"/api/admin/users/{Org.Admin.Id}", csrf, new { is_active = false })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    private User AddUser(string name, Role role)
    {
        using var db = _host.Db();
        var u = new User
        {
            EmployeeCode = "X" + name[..3].ToUpperInvariant(), FullName = name, Email = $"{name.ToLowerInvariant().Replace(' ', '.')}@test.example",
            PasswordHash = AuthHost.PythonHash, Role = role, DateJoined = new DateOnly(2025, 1, 1),
        };
        db.Users.Add(u);
        db.SaveChanges();
        return u;
    }

    [Fact]
    public async Task Last_admin_rule_with_second_admin()
    {
        var other = AddUser("Second Admin", Role.Admin);
        var csrf = await _host.LoginAsync(Org.Admin);
        (await Patch($"/api/admin/users/{other.Id}", csrf, new { role = "MANAGER" })).Status.Should().Be(HttpStatusCode.OK);
        var third = AddUser("Third Admin", Role.Admin);
        csrf = await _host.LoginAsync(third);
        var (status, _) = await Patch($"/api/admin/users/{Org.Admin.Id}", csrf, new { role = "MANAGER" });
        status.Should().Be(HttpStatusCode.OK);
        (await Patch($"/api/admin/users/{third.Id}", csrf, new { role = "MANAGER" })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var db = _host.Db();
        db.AuditLogs.Count(a => a.Action == "USER_ROLE_CHANGED").Should().Be(2);
    }

    [Fact]
    public async Task Cannot_demote_manager_with_reports_or_deactivate()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        (await Patch($"/api/admin/users/{Org.Priya.Id}", csrf, new { role = "EMPLOYEE" })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Patch($"/api/admin/users/{Org.Priya.Id}", csrf, new { is_active = false })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Deactivation_revokes_sessions()
    {
        using var other = _host.Api.CreateClient();
        (await other.PostAsJsonAsync("/api/auth/login", new { email = Org.Rahul.Email, password = AuthHost.Password })).EnsureSuccessStatusCode();
        var csrf = await _host.LoginAsync(Org.Admin);
        var (status, body) = await Patch($"/api/admin/users/{Org.Rahul.Id}", csrf, new { is_active = false });
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (await other.GetAsync(new Uri("/api/auth/me", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var db = _host.Db();
        db.AuditLogs.Single(a => a.Action == "USER_DEACTIVATED").AfterValues!["is_active"]!.GetValue<bool>().Should().BeFalse();
    }

    [Fact]
    public async Task Employee_cannot_escalate_privileges()
    {
        var csrf = await _host.LoginAsync(Org.Rahul);
        (await Patch($"/api/admin/users/{Org.Rahul.Id}", csrf, new { role = "ADMIN" })).Status.Should().Be(HttpStatusCode.Forbidden);
        csrf = await _host.LoginAsync(Org.Priya);
        (await Patch($"/api/admin/users/{Org.Rahul.Id}", csrf, new { role = "ADMIN" })).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/api/admin/users")]
    [InlineData("/api/admin/audit-logs")]
    [InlineData("/api/admin/schedules")]
    [InlineData("/api/managers")]
    [InlineData("/api/admin/sites")]
    [InlineData("/api/admin/devices")]
    public async Task Non_admins_cannot_use_admin_endpoints(string path)
    {
        await _host.LoginAsync(Org.Priya);
        (await _host.Client.GetAsync(new Uri(path, UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Users_are_listed_filtered_and_paged()
    {
        await _host.LoginAsync(Org.Admin);
        var all = await Get("/api/admin/users?page_size=3");
        (all.GetProperty("total").GetInt32(), all.GetProperty("items").GetArrayLength()).Should().Be((7, 3));
        all.GetProperty("items")[0].GetProperty("full_name").GetString().Should().Be("Admin User"); // by name
        (await Get("/api/admin/users?search=KUMAR")).GetProperty("total").GetInt32().Should().Be(2);
        (await Get($"/api/admin/users?manager_id={Org.Priya.Id}")).GetProperty("total").GetInt32().Should().Be(2);
        (await Get("/api/admin/users?role=MANAGER&is_active=true")).GetProperty("total").GetInt32().Should().Be(3);
        var bad = await Get("/api/admin/users?page=0&role=NOPE&is_active=maybe");
        bad.GetProperty("error").GetProperty("details").EnumerateArray().Select(d => d.GetProperty("field").GetString())
            .Should().Equal("query.page", "query.role", "query.is_active");
        (await Get("/api/managers")).EnumerateArray().Select(u => u.GetProperty("full_name").GetString())
            .Should().Equal("Admin User", "Anil Kumar", "Priya Sharma", "Vikram Desai");
    }

    [Fact]
    public async Task Department_crud_and_unique_names()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        var (status, body) = await Post("/api/admin/departments", csrf, new { name = "Finance", department_head_id = Org.Anil.Id });
        (status, body.GetProperty("head").GetProperty("id").GetInt32()).Should().Be((HttpStatusCode.Created, Org.Anil.Id));
        (await Post("/api/admin/departments", csrf, new { name = "finance" })).Status.Should().Be(HttpStatusCode.Conflict);
        var id = body.GetProperty("id").GetInt32();
        (await Patch($"/api/admin/departments/{id}", csrf, new { description = "Money" })).Status.Should().Be(HttpStatusCode.OK);
        (await _host.Send(HttpMethod.Delete, $"/api/admin/departments/{Org.Ai.Id}", csrf)).Status.Should().Be(HttpStatusCode.Conflict); // has members
        var (deleted, message) = await _host.Send(HttpMethod.Delete, $"/api/admin/departments/{id}", csrf);
        (deleted, message.GetProperty("message").GetString()).Should().Be((HttpStatusCode.OK, "Department deleted"));
    }

    [Fact]
    public async Task Schedule_validation_and_overlap()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        var body = new Dictionary<string, object?>
        {
            ["name"] = "Late shift", ["employee_id"] = Org.Rahul.Id, ["start_time"] = "12:00", ["end_time"] = "20:00", ["required_minutes"] = 420,
            ["working_days"] = new[] { 4, 1, 2, 3 }, ["effective_from"] = "2026-03-01",
        };
        var (status, created) = await Post("/api/admin/schedules", csrf, body);
        status.Should().Be(HttpStatusCode.Created, created.ToString());
        created.GetProperty("working_days").EnumerateArray().Select(d => d.GetInt32()).Should().Equal(1, 2, 3, 4);
        created.GetProperty("target_label").GetString().Should().Be("Employee: Rahul Reddy");
        var (overlap, problem) = await Post("/api/admin/schedules", csrf, new Dictionary<string, object?>(body) { ["effective_from"] = "2026-04-01" });
        (overlap, problem.GetProperty("error").GetProperty("message").GetString()).Should()
            .Be((HttpStatusCode.Conflict, "Overlaps the effective dates of schedule 'Late shift'"));
        (await Post("/api/admin/schedules", csrf, new Dictionary<string, object?>(body) { ["employee_id"] = Org.Sneha.Id, ["working_days"] = new[] { 8 } }))
            .Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        // The schedule change is reflected in calculations: Friday is no longer a working day for Rahul.
        var s = await Get($"/api/attendance/employees/{Org.Rahul.Id}/summary?start=2026-03-06&end=2026-03-06");
        (s.GetProperty("weekend_days").GetInt32(), s.GetProperty("expected_days").GetInt32()).Should().Be((1, 0));
        var id = created.GetProperty("id").GetInt32();
        (await Patch($"/api/admin/schedules/{id}", csrf, new { effective_to = "2026-02-01" })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await Patch($"/api/admin/schedules/{id}", csrf, new { })).Status.Should().Be(HttpStatusCode.OK);
        using (var db = _host.Db())
        {
            db.AuditLogs.Count(a => a.Action == "SCHEDULE_UPDATED").Should().Be(1); // recorded even without changes, as in Python
        }

        (await _host.Send(HttpMethod.Delete, $"/api/admin/schedules/{id}", csrf)).Status.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Holiday_management()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        (await Post("/api/admin/holidays", csrf, new { name = "Festival", holiday_date = "2026-03-04" })).Status.Should().Be(HttpStatusCode.Created);
        (await Post("/api/admin/holidays", csrf, new { name = "Dup", holiday_date = "2026-03-04" })).Status.Should().Be(HttpStatusCode.Conflict);
        var s = await Get($"/api/attendance/employees/{Org.Rahul.Id}/summary?start=2026-03-04&end=2026-03-04");
        (s.GetProperty("holiday_days").GetInt32(), s.GetProperty("absent_days").GetInt32()).Should().Be((1, 0));
    }

    [Fact]
    public async Task Policy_update_is_partial_and_audited()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        var (status, body) = await _host.Send(HttpMethod.Put, "/api/admin/policy", csrf, new { late_grace_minutes = 20, notes = "Reviewed", manager_can_approve = (bool?)null });
        status.Should().Be(HttpStatusCode.OK);
        (body.GetProperty("late_grace_minutes").GetInt32(), body.GetProperty("notes").GetString(), body.GetProperty("manager_can_approve").GetBoolean())
            .Should().Be((20, "Reviewed", true));
        (await _host.Send(HttpMethod.Put, "/api/admin/policy", csrf, new { max_session_minutes = 30 })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        using var db = _host.Db();
        db.AuditLogs.Single(a => a.Action == "POLICY_UPDATED").AfterValues!.ToJsonString().Should().Be("""{"late_grace_minutes":20,"notes":"Reviewed"}""");
    }

    [Fact]
    public async Task Audit_log_listing_and_filter()
    {
        var csrf = await _host.LoginAsync(Org.Admin);
        await Post("/api/admin/departments", csrf, new { name = "Legal" });
        var logs = await Get("/api/admin/audit-logs?action=DEPARTMENT_CREATED");
        (logs.GetProperty("total").GetInt32(), logs.GetProperty("items")[0].GetProperty("actor_name").GetString()).Should().Be((1, "Admin User"));
        (await Get("/api/admin/audit-logs?search=admin%20user")).GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(2);
    }
}
