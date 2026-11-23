using System.Net;
using System.Text.Json;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Tests.Auth;
using Ats.Tests.Uploads;
using FluentAssertions;

namespace Ats.Tests.Attendance;

/// <summary>
/// Dashboards, role and reporting-scope enforcement and the WFO reads (ports of test_dashboards_reports.py, test_permissions.py
/// and the read tests of test_wfo_api.py). Clock: Tuesday 10 March 2026, 12:00 IST.
/// </summary>
public sealed class DashboardApiTests : IDisposable
{
    private const string Range = "start=2026-03-09&end=2026-03-10";
    private const string WideRange = "start=2026-03-01&end=2026-03-10";
    private static readonly DateOnly Mon = new(2026, 3, 9), Tue = new(2026, 3, 10);
    private readonly AuthHost _host = new();

    public DashboardApiTests() => _host.Clock.UtcNow = TestData.Tuesday;

    public void Dispose() => _host.Dispose();

    private Auth.Org Org => _host.Org;

    private Task<HttpResponseMessage> Raw(string path) => _host.Client.GetAsync(new Uri(path, UriKind.Relative));

    private async Task<JsonElement> Get(string path) => await WriteClient.Json(await Raw(path));

    private static List<int> Ids(JsonElement rows, string key = "employee_id") => [.. rows.EnumerateArray().Select(r => r.GetProperty(key).GetInt32()).Order()];

    // ------------------------------------------------------------------ dashboards

    [Fact]
    public async Task Overview_snapshot_categories_are_exclusive()
    {
        TestData.Timesheet(_host, Org.Rahul, Tue);
        TestData.Timesheet(_host, Org.Sneha, Tue, WorkMode.Wfh);
        TestData.Timesheet(_host, Org.Arjun, Tue, WorkMode.Leave);
        using (var db = _host.Db())
        {
            // A row left in the legacy leave table (feature removed) must not change anyone's status.
            db.LeaveRecords.Add(new LeaveRecord { EmployeeId = Org.Vikram.Id, StartDate = Tue, EndDate = Tue, LeaveType = LeaveType.Sick, Status = RequestStatus.Approved });
            db.SaveChanges();
        }

        await _host.LoginAsync(Org.Admin);
        var o = await Get($"/api/dashboard/overview?{Range}");
        var snap = o.GetProperty("snapshot");
        o.GetProperty("headcount").GetInt32().Should().Be(7);
        snap.TryGetProperty("on_leave", out _).Should().BeFalse();
        o.TryGetProperty("pending_leave", out _).Should().BeFalse();
        (snap.GetProperty("present").GetInt32(), snap.GetProperty("wfh").GetInt32(), snap.GetProperty("leave").GetInt32(), snap.GetProperty("no_timesheet").GetInt32())
            .Should().Be((1, 1, 1, 4));
        new[] { "present", "wfh", "absent", "leave", "no_timesheet", "off" }.Sum(k => snap.GetProperty(k).GetInt32()).Should().Be(7);
        o.GetProperty("timesheet_through").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Untracked_accounts_excluded_from_stats()
    {
        using (var db = _host.Db())
        {
            db.Users.Single(u => u.Id == Org.Admin.Id).AttendanceTracked = false;
            db.SaveChanges();
        }

        await _host.LoginAsync(Org.Admin);
        (await Get($"/api/dashboard/overview?{Range}")).GetProperty("headcount").GetInt32().Should().Be(6);
    }

    [Fact]
    public async Task Employee_rows_search_filter_sort()
    {
        TestData.Timesheet(_host, Org.Rahul, Mon, minutes: 480);
        await _host.LoginAsync(Org.Priya);
        (await Get($"/api/dashboard/employees?{Range}&search=sne")).GetProperty("items").EnumerateArray().Select(r => r.GetProperty("full_name").GetString())
            .Should().Equal("Sneha Rao");
        var rows = (await Get($"/api/dashboard/employees?{Range}&sort=net_minutes&order=desc")).GetProperty("items");
        (rows[0].GetProperty("full_name").GetString(), rows[0].GetProperty("period").GetProperty("net_minutes").GetInt32()).Should().Be(("Rahul Reddy", 480));
        (await Get($"/api/dashboard/employees?{Range}&status=NO_TIMESHEET")).GetProperty("items").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Trends_and_exceptions()
    {
        TestData.Cover(_host, new DateOnly(2026, 3, 2), Mon, Org.Rahul, Org.Sneha);
        // 10:30 IST in, 5 h: late and short. Sneha is not in the timesheet: absent.
        TestData.Timesheet(_host, Org.Rahul, Mon, minutes: 300, firstIn: new DateTimeOffset(2026, 3, 9, 5, 0, 0, TimeSpan.Zero));
        await _host.LoginAsync(Org.Priya);
        var t = await Get("/api/dashboard/trends?start=2026-03-02&end=2026-03-10&granularity=week");
        (t.GetProperty("items").GetArrayLength(), t.GetProperty("headcount").GetInt32()).Should().Be((2, 2));
        var items = t.GetProperty("items");
        (items[0].GetProperty("absent").GetInt32(), items[1].GetProperty("present").GetInt32(), items[1].GetProperty("attended").GetInt32()).Should().Be((10, 1, 1));
        var e = await Get($"/api/dashboard/exceptions?{Range}");
        e.GetProperty("counts").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32())
            .Should().BeEquivalentTo(new Dictionary<string, int> { ["LATE_ARRIVAL"] = 1, ["SHORT_HOURS"] = 1, ["ABSENT"] = 1 });
        var late = await Get($"/api/dashboard/exceptions?{Range}&exception_type=LATE_ARRIVAL");
        late.GetProperty("total").GetInt32().Should().Be(1);
        late.GetProperty("items")[0].GetProperty("first_in").GetString().Should().StartWith("2026-03-09T05:00");
    }

    [Fact]
    public async Task Department_and_manager_comparison()
    {
        await _host.LoginAsync(Org.Admin);
        var ai = (await Get($"/api/dashboard/departments?{Range}")).EnumerateArray().First(d => d.GetProperty("department_name").GetString() == "AI Engineering");
        (ai.GetProperty("headcount").GetInt32(), ai.GetProperty("head_name").ValueKind).Should().Be((3, JsonValueKind.Null));
        var managers = (await Get($"/api/dashboard/managers?{Range}")).EnumerateArray().ToDictionary(m => m.GetProperty("manager_name").GetString()!);
        managers["Priya Sharma"].GetProperty("team_size").GetInt32().Should().Be(2);
        (managers["Vikram Desai"].GetProperty("team_size").GetInt32(), managers["Vikram Desai"].GetProperty("direct_reports").GetInt32()).Should().Be((5, 2));
    }

    [Fact]
    public async Task Employee_trends_endpoint()
    {
        TestData.Timesheet(_host, Org.Rahul, Mon, minutes: 480);
        await _host.LoginAsync(Org.Rahul);
        var t = await Get($"/api/attendance/employees/{Org.Rahul.Id}/trends?start=2026-02-01&end=2026-03-10&granularity=month");
        t.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("label").GetString()).Should().Equal("Feb 2026", "Mar 2026");
        (t.GetProperty("items")[1].GetProperty("net_minutes").GetInt32(), t.GetProperty("items")[1].GetProperty("present").GetInt32()).Should().Be((480, 1));
        (await Raw($"/api/attendance/employees/{Org.Sneha.Id}/trends")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Org_hierarchy()
    {
        await _host.LoginAsync(Org.Admin);
        var ai = (await Get("/api/org/hierarchy")).GetProperty("departments").EnumerateArray().First(d => d.GetProperty("name").GetString() == "AI Engineering");
        var priya = ai.GetProperty("nodes")[0];
        priya.GetProperty("full_name").GetString().Should().Be("Priya Sharma");
        priya.GetProperty("children").EnumerateArray().Select(c => c.GetProperty("full_name").GetString()).Order().Should().Equal("Rahul Reddy", "Sneha Rao");
    }

    // ------------------------------------------------------------------ permissions

    [Fact]
    public async Task Employee_can_read_only_own_attendance()
    {
        await _host.LoginAsync(Org.Rahul);
        (await Raw($"/api/attendance/employees/{Org.Rahul.Id}/summary?{WideRange}")).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var other in new[] { Org.Sneha, Org.Priya, Org.Arjun })
        {
            (await Raw($"/api/attendance/employees/{other.Id}/summary?{WideRange}")).StatusCode.Should().Be(HttpStatusCode.NotFound); // like a missing employee
        }
    }

    [Theory]
    [InlineData("/api/dashboard/overview")]
    [InlineData("/api/dashboard/employees")]
    [InlineData("/api/dashboard/trends")]
    [InlineData("/api/team/members")]
    [InlineData("/api/reports/team-attendance.csv")]
    public async Task Employee_cannot_use_team_endpoints(string path)
    {
        await _host.LoginAsync(Org.Rahul);
        (await Raw($"{path}?{WideRange}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData("/api/admin/users")]
    [InlineData("/api/admin/audit-logs")]
    [InlineData("/api/dashboard/departments")]
    [InlineData("/api/org/hierarchy")]
    [InlineData("/api/admin/schedules")]
    public async Task Non_admins_cannot_use_admin_endpoints(string path)
    {
        await _host.LoginAsync(Org.Priya);
        (await Raw($"{path}?{WideRange}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Manager_sees_only_reporting_subtree()
    {
        await _host.LoginAsync(Org.Priya);
        Ids((await Get($"/api/dashboard/employees?{WideRange}")).GetProperty("items")).Should().Equal(new[] { Org.Rahul.Id, Org.Sneha.Id }.Order());
        (await Raw($"/api/attendance/employees/{Org.Rahul.Id}/summary?{WideRange}")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Raw($"/api/attendance/employees/{Org.Arjun.Id}/summary?{WideRange}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Raw($"/api/attendance/employees/{Org.Anil.Id}/summary?{WideRange}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Nested_manager_sees_whole_subtree()
    {
        await _host.LoginAsync(Org.Vikram);
        Ids((await Get($"/api/dashboard/employees?{WideRange}&page_size=50")).GetProperty("items"))
            .Should().Equal(new[] { Org.Priya.Id, Org.Anil.Id, Org.Rahul.Id, Org.Sneha.Id, Org.Arjun.Id }.Order());
        Ids((await Get($"/api/dashboard/employees?{WideRange}&manager_id={Org.Priya.Id}")).GetProperty("items")).Should().Equal(new[] { Org.Rahul.Id, Org.Sneha.Id }.Order());
    }

    [Fact]
    public async Task Manager_cannot_query_unrelated_manager_scope()
    {
        await _host.LoginAsync(Org.Priya);
        (await Raw($"/api/dashboard/employees?{WideRange}&manager_id={Org.Anil.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await Raw($"/api/reports/team-attendance.csv?{WideRange}&manager_id={Org.Anil.Id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Reassignment_changes_access_immediately()
    {
        await _host.LoginAsync(Org.Priya);
        (await Raw($"/api/attendance/employees/{Org.Arjun.Id}/summary?{WideRange}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        using (var db = _host.Db())
        {
            db.Users.Single(u => u.Id == Org.Arjun.Id).ManagerId = Org.Priya.Id;
            db.SaveChanges();
        }

        (await Raw($"/api/attendance/employees/{Org.Arjun.Id}/summary?{WideRange}")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Admin_sees_everyone()
    {
        await _host.LoginAsync(Org.Admin);
        (await Get($"/api/dashboard/employees?{WideRange}&page_size=50")).GetProperty("items").GetArrayLength().Should().Be(7);
        Ids((await Get($"/api/dashboard/employees?{WideRange}&department_id={Org.Ai.Id}")).GetProperty("items"))
            .Should().Equal(new[] { Org.Priya.Id, Org.Rahul.Id, Org.Sneha.Id }.Order());
    }

    // ------------------------------------------------------------------ WFO reads

    private static JsonElement Day(JsonElement body, string date) => WriteClient.Day(body, date);

    [Fact]
    public async Task Me_wfo_classifies_timesheet_days()
    {
        TestData.Worked(_host, Org.Rahul, new DateOnly(2026, 3, 9), location: WorkMode.Office);
        TestData.Worked(_host, Org.Rahul, new DateOnly(2026, 3, 6), location: WorkMode.Wfh);
        TestData.Worked(_host, Org.Rahul, new DateOnly(2026, 3, 5)); // no work mode: hours without an office signal
        TestData.Worked(_host, Org.Rahul, new DateOnly(2026, 3, 4), hours: 3, location: WorkMode.Office); // below the 4 h minimum
        await _host.LoginAsync(Org.Rahul);
        var body = await Get("/api/me/wfo?month=2026-03");
        (Day(body, "2026-03-09").GetProperty("work_mode").GetString(), Day(body, "2026-03-09").GetProperty("wfo_source").GetString()).Should().Be(("OFFICE", "UPLOAD_COLUMN"));
        Day(body, "2026-03-09").GetProperty("is_wfo_day").GetBoolean().Should().BeTrue();
        Day(body, "2026-03-06").GetProperty("wfo_source").GetString().Should().Be("UPLOAD_COLUMN");
        (Day(body, "2026-03-05").GetProperty("work_mode").GetString(), Day(body, "2026-03-05").GetProperty("wfo_source").GetString()).Should().Be(("WFH", "INFERRED"));
        (Day(body, "2026-03-04").GetProperty("work_mode").GetString(), Day(body, "2026-03-04").GetProperty("is_wfo_day").GetBoolean()).Should().Be(("OFFICE", false));
        Day(body, "2026-03-07").GetProperty("work_mode").GetString().Should().Be("WEEKOFF");
        (Day(body, "2026-03-10").GetProperty("in_progress").GetBoolean(), Day(body, "2026-03-11").GetProperty("future").GetBoolean()).Should().Be((true, true));
        var s = body.GetProperty("summary");
        (s.GetProperty("wfo_days").GetInt32(), s.GetProperty("wfh_days").GetInt32(), s.GetProperty("office_days_below_minimum").GetInt32(), s.GetProperty("working_days").GetInt32())
            .Should().Be((1, 2, 1, 6));
        body.GetProperty("policy").GetRawText().Should().Be("""{"min_days_per_week":3,"min_days_per_month":12,"min_office_minutes":240}""");
        var weeks = body.GetProperty("weekly_wfo_breakdown");
        (weeks[0].GetProperty("status").GetString(), weeks[1].GetProperty("status").GetString(), weeks[0].GetProperty("week_start").GetString())
            .Should().Be(("SHORT", "SHORT", "2026-02-23"));
    }

    [Fact]
    public async Task Wfo_role_boundaries()
    {
        (await Raw("/api/me/wfo")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await _host.LoginAsync(Org.Rahul);
        (await Raw("/api/me/wfo?month=2026-03")).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var path in new[] { "/api/team/wfo?month=2026-03", "/api/org/wfo?month=2026-03", $"/api/team/wfo/employees/{Org.Sneha.Id}?month=2026-03" })
        {
            (await Raw(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
        }

        await _host.LoginAsync(Org.Priya);
        (await Get("/api/team/wfo?month=2026-03")).GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("employee").GetProperty("full_name").GetString())
            .Should().BeEquivalentTo("Priya Sharma", "Rahul Reddy", "Sneha Rao"); // own + reporting line, never Arjun
        (await Raw($"/api/team/wfo/employees/{Org.Rahul.Id}?month=2026-03")).StatusCode.Should().Be(HttpStatusCode.OK);
        foreach (var path in new[]
                 {
                     $"/api/team/wfo/employees/{Org.Arjun.Id}?month=2026-03", $"/api/team/wfo?month=2026-03&employee_id={Org.Arjun.Id}",
                     "/api/team/wfo/employees/999999?month=2026-03", "/api/org/wfo?month=2026-03", "/api/org/wfo/export.xlsx?month=2026-03",
                 })
        {
            (await Raw(path)).StatusCode.Should().Be(HttpStatusCode.Forbidden, path);
        }

        await _host.LoginAsync(Org.Vikram); // whole reporting line, including indirect reports
        (await Get("/api/team/wfo?month=2026-03")).GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("employee").GetProperty("full_name").GetString())
            .Should().BeEquivalentTo("Vikram Desai", "Priya Sharma", "Anil Kumar", "Rahul Reddy", "Sneha Rao", "Arjun Kumar");
        await _host.LoginAsync(Org.Admin);
        (await Raw($"/api/team/wfo/employees/{Org.Arjun.Id}?month=2026-03")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Raw("/api/me/wfo?month=2026-13")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Org_wfo_groups_by_direct_manager_and_filters()
    {
        TestData.Worked(_host, Org.Rahul, Mon, location: WorkMode.Office);
        await _host.LoginAsync(Org.Admin);
        var body = await Get("/api/org/wfo?month=2026-03");
        var groups = body.GetProperty("groups").EnumerateArray().ToDictionary(
            g => g.GetProperty("manager_name").GetString()!,
            g => g.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("employee").GetProperty("full_name").GetString()!).ToHashSet());
        groups["Priya Sharma"].Should().BeEquivalentTo("Rahul Reddy", "Sneha Rao");
        groups["Anil Kumar"].Should().BeEquivalentTo("Arjun Kumar");
        groups["Vikram Desai"].Should().BeEquivalentTo("Priya Sharma", "Anil Kumar");
        var totals = body.GetProperty("totals");
        totals.GetProperty("headcount").GetInt32().Should().Be(groups.Values.Sum(v => v.Count));
        totals.GetProperty("wfo_days").GetInt32().Should().Be(1);
        (totals.GetProperty("compliant").GetInt32() + totals.GetProperty("non_compliant").GetInt32()).Should().Be(totals.GetProperty("headcount").GetInt32());
        (await Get($"/api/org/wfo?month=2026-03&manager_id={Org.Priya.Id}")).GetProperty("groups").EnumerateArray()
            .SelectMany(g => g.GetProperty("rows").EnumerateArray()).Select(r => r.GetProperty("employee").GetProperty("full_name").GetString())
            .Should().BeEquivalentTo("Rahul Reddy", "Sneha Rao");
        (await Get($"/api/org/wfo?month=2026-03&department_id={Org.Se.Id}")).GetProperty("groups").EnumerateArray()
            .SelectMany(g => g.GetProperty("rows").EnumerateArray()).Select(r => r.GetProperty("employee").GetProperty("full_name").GetString())
            .Should().BeEquivalentTo("Anil Kumar", "Arjun Kumar");
    }

    [Fact]
    public async Task Existing_summary_includes_wfo()
    {
        TestData.Worked(_host, Org.Rahul, Mon, location: WorkMode.Office);
        await _host.LoginAsync(Org.Rahul);
        var wfo = (await Get($"/api/attendance/employees/{Org.Rahul.Id}/summary?{WideRange}")).GetProperty("wfo");
        wfo.GetProperty("wfo_days").GetInt32().Should().Be(1);
        wfo.GetProperty("compliance").GetString().Should().BeOneOf("COMPLIANT", "NON_COMPLIANT");
        (wfo.TryGetProperty("weekly_wfo_breakdown", out _), wfo.TryGetProperty("wfo_percentage", out _)).Should().Be((true, true));
    }

    [Theory]
    [InlineData("2026-3")]
    [InlineData("26-03")]
    [InlineData("2026-03-01")]
    public async Task Month_parameter_is_validated(string month)
    {
        await _host.LoginAsync(Org.Rahul);
        (await Raw($"/api/me/wfo?month={month}")).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }
}
