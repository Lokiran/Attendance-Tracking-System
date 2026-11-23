using System.Net;
using System.Text;
using System.Text.Json;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Tests.Auth;
using Ats.Tests.Uploads;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ats.Tests.Attendance;

/// <summary>
/// Attendance reads driven by imported timesheets (port of backend/app/tests/test_attendance_api.py). Check-in / check-out and
/// corrections are gone; the legacy tables keep their rules.
/// </summary>
public sealed class AttendanceApiTests : IDisposable
{
    private const string Url = "/api/admin/attendance-uploads";
    private readonly AuthHost _host = new();

    public AttendanceApiTests() => _host.Clock.UtcNow = TestData.Tuesday;

    public void Dispose() => _host.Dispose();

    private Auth.Org Org => _host.Org;

    private async Task<JsonElement> Get(string path) => await WriteClient.Json(await _host.Client.GetAsync(new Uri(path, UriKind.Relative)));

    private async Task<JsonElement> Import(string csrf, string csv, string? query = null)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "timesheet.csv" } };
        using var request = new HttpRequestMessage(HttpMethod.Post, Url + (query is null ? "" : "?" + query)) { Content = form };
        request.Headers.Add("X-CSRF-Token", csrf);
        var up = await WriteClient.Json(await _host.Client.SendAsync(request));
        var (status, body) = await _host.Send(HttpMethod.Post, $"{Url}/{up.GetProperty("id").GetInt32()}/commit", csrf, new { });
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        return body;
    }

    [Fact]
    public async Task Check_in_check_out_and_corrections_are_gone()
    {
        var csrf = await _host.LoginAsync(Org.Rahul);
        foreach (var path in new[] { "/api/attendance/check-in", "/api/attendance/check-out", "/api/corrections" })
        {
            (await _host.Send(HttpMethod.Post, path, csrf, new { })).Status.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.MethodNotAllowed);
        }

        (await _host.Client.GetAsync(new Uri("/api/corrections", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Monthly_timesheet_drives_attendance()
    {
        _host.Clock.UtcNow = TestData.NoonIst(new DateOnly(2026, 11, 10));
        var csrf = await _host.LoginAsync(Org.Admin);
        var code = Org.Rahul.EmployeeCode;
        var up = await Import(csrf, $"Employee ID,Date\n{code},01/09/2026\n{code},02/09/2026\n{code},03/09/2026\n");
        (up.GetProperty("period_start").GetString(), up.GetProperty("period_end").GetString()).Should().Be(("2026-09-01", "2026-09-30"));
        await Import(csrf, $"Employee ID,Date\n{code},04/09/2026\n", "presence_mode=WFH");

        await _host.LoginAsync(Org.Rahul);
        var s = await Get($"/api/attendance/employees/{Org.Rahul.Id}/summary?start=2026-09-01&end=2026-09-30");
        (s.GetProperty("present_days").GetInt32(), s.GetProperty("wfh_days").GetInt32(), s.GetProperty("expected_days").GetInt32()).Should().Be((3, 1, 22));
        (s.GetProperty("absent_days").GetInt32(), s.GetProperty("no_timesheet_days").GetInt32()).Should().Be((18, 0));
        (s.GetProperty("hours_days").GetInt32(), s.GetProperty("average_net_minutes").ValueKind).Should().Be((0, JsonValueKind.Null)); // no hours in a presence list
        s.GetProperty("wfo").GetProperty("wfo_days").GetInt32().Should().Be(3);

        var october = await Get($"/api/attendance/employees/{Org.Rahul.Id}/summary?start=2026-10-01&end=2026-10-31");
        (october.GetProperty("absent_days").GetInt32(), october.GetProperty("no_timesheet_days").GetInt32()).Should().Be((0, 22)); // no October sheet yet
        october.GetProperty("attendance_percentage").ValueKind.Should().Be(JsonValueKind.Null);

        var days = (await Get($"/api/attendance/employees/{Org.Rahul.Id}/days?start=2026-09-01&end=2026-09-07&order=asc")).GetProperty("items");
        days.EnumerateArray().Select(d => d.GetProperty("status").GetString()).Should().Equal("PRESENT", "PRESENT", "PRESENT", "WFH", "WEEKEND", "WEEKEND", "ABSENT");
        (days[0].GetProperty("hours_known").GetBoolean(), days[0].GetProperty("work_mode").GetString()).Should().Be((false, "OFFICE"));

        var current = await Get("/api/attendance/current");
        (current.GetProperty("timesheet_through").GetString(), current.GetProperty("schedule").GetProperty("required_minutes").GetInt32()).Should().Be(("2026-09-30", 480));
        current.TryGetProperty("active_session", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Hours_and_times_from_the_timesheet()
    {
        _host.Clock.UtcNow = TestData.NoonIst(new DateOnly(2026, 11, 10));
        var csrf = await _host.LoginAsync(Org.Admin);
        var code = Org.Sneha.EmployeeCode;
        await Import(csrf, $"Employee ID,Date,In,Out\n{code},01/10/2026,10:05,17:05\n{code},02/10/2026,09:30,18:30\n");
        await _host.LoginAsync(Org.Sneha);
        var day = await Get($"/api/attendance/employees/{Org.Sneha.Id}/days/2026-10-01");
        var rec = day.GetProperty("record");
        (rec.GetProperty("status").GetString(), rec.GetProperty("net_minutes").GetInt32(), rec.GetProperty("hours_known").GetBoolean()).Should().Be(("PRESENT", 420, true));
        rec.GetProperty("exceptions").EnumerateArray().Select(e => e.GetString()).Should().BeEquivalentTo("LATE_ARRIVAL", "SHORT_HOURS");
        (day.GetProperty("timesheet").GetProperty("upload_filename").GetString(), day.GetProperty("timesheet").GetProperty("work_mode").GetString())
            .Should().Be(("timesheet.csv", "OFFICE"));
        day.GetProperty("shift").GetProperty("start_time").GetString().Should().NotBeNullOrEmpty();
        day.GetProperty("shift").GetProperty("required_minutes").GetInt32().Should().BePositive();
        day.GetProperty("late_grace_minutes").GetInt32().Should().BeGreaterThanOrEqualTo(0);
        var s = await Get($"/api/attendance/employees/{Org.Sneha.Id}/summary?start=2026-10-01&end=2026-10-02");
        (s.GetProperty("net_minutes").GetInt32(), s.GetProperty("hours_required_minutes").GetInt32(), s.GetProperty("variance_minutes").GetInt32()).Should().Be((960, 960, 0));
        (s.GetProperty("hours_days").GetInt32(), s.GetProperty("average_net_minutes").GetInt32(), s.GetProperty("late_days").GetInt32()).Should().Be((2, 480, 1));
    }

    [Fact]
    public async Task Partial_month_sheet_covers_up_to_the_day_before_import()
    {
        _host.Clock.UtcNow = TestData.NoonIst(new DateOnly(2026, 11, 10));
        var csrf = await _host.LoginAsync(Org.Admin);
        var up = await Import(csrf, $"Employee ID,Date\n{Org.Rahul.EmployeeCode},03/11/2026\n");
        (up.GetProperty("period_start").GetString(), up.GetProperty("period_end").GetString()).Should().Be(("2026-11-01", "2026-11-09"));
        await _host.LoginAsync(Org.Rahul);
        var days = (await Get($"/api/attendance/employees/{Org.Rahul.Id}/days?start=2026-11-02&end=2026-11-10")).GetProperty("items").EnumerateArray()
            .ToDictionary(d => d.GetProperty("date").GetString()!, d => d.GetProperty("status").GetString());
        (days["2026-11-03"], days["2026-11-09"], days["2026-11-10"]).Should().Be(("PRESENT", "ABSENT", "NO_TIMESHEET"));
    }

    [Fact]
    public async Task A_timesheet_only_covers_the_people_listed_in_it()
    {
        _host.Clock.UtcNow = TestData.NoonIst(new DateOnly(2026, 11, 10));
        var csrf = await _host.LoginAsync(Org.Admin);
        await Import(csrf, $"Employee ID,Date\n{Org.Rahul.EmployeeCode},01/10/2026\n");
        var s = await Get($"/api/attendance/employees/{Org.Sneha.Id}/summary?start=2026-10-01&end=2026-10-31");
        (s.GetProperty("absent_days").GetInt32(), s.GetProperty("no_timesheet_days").GetInt32()).Should().Be((0, 22));
        var r = await Get($"/api/attendance/employees/{Org.Rahul.Id}/summary?start=2026-10-01&end=2026-10-31");
        (r.GetProperty("present_days").GetInt32(), r.GetProperty("absent_days").GetInt32()).Should().Be((1, 21));
        (await Get("/api/attendance/current")).GetProperty("timesheet_through").GetString().Should().Be("2026-10-31");
        await _host.LoginAsync(Org.Sneha);
        var current = await Get("/api/attendance/current");
        (current.GetProperty("timesheet_through").GetString(), current.GetProperty("my_timesheet_through").ValueKind).Should().Be(("2026-10-31", JsonValueKind.Null));
    }

    [Fact]
    public async Task Admin_override_counts_without_a_sheet()
    {
        _host.Clock.UtcNow = TestData.NoonIst(new DateOnly(2026, 11, 10));
        var csrf = await _host.LoginAsync(Org.Admin);
        (await _host.Send(HttpMethod.Put, $"/api/admin/attendance/{Org.Arjun.Id}/2026-11-04/work-mode", csrf, new { work_mode = "OFFICE", note = "Client visit" }))
            .Status.Should().Be(HttpStatusCode.OK);
        var day = await Get($"/api/attendance/employees/{Org.Arjun.Id}/days/2026-11-04");
        (day.GetProperty("record").GetProperty("status").GetString(), day.GetProperty("timesheet").GetProperty("override_note").GetString())
            .Should().Be(("PRESENT", "Client visit"));
        TestData.Timesheet(_host, Org.Arjun, new DateOnly(2026, 11, 5), WorkMode.Leave);
        (await Get($"/api/attendance/employees/{Org.Arjun.Id}/days/2026-11-05")).GetProperty("record").GetProperty("status").GetString().Should().Be("LEAVE");
    }

    [Fact]
    public async Task History_pagination_and_invalid_range()
    {
        await _host.LoginAsync(Org.Rahul);
        var body = await Get($"/api/attendance/employees/{Org.Rahul.Id}/days?start=2026-03-01&end=2026-03-10&page_size=3");
        (body.GetProperty("total").GetInt32(), body.GetProperty("items").GetArrayLength(), body.GetProperty("items")[0].GetProperty("date").GetString())
            .Should().Be((10, 3, "2026-03-10"));
        (await _host.Client.GetAsync(new Uri($"/api/attendance/employees/{Org.Rahul.Id}/days?start=2026-03-10&end=2026-03-01", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await _host.Client.GetAsync(new Uri($"/api/attendance/employees/{Org.Rahul.Id}/days?start=2024-01-01&end=2026-03-01", UriKind.Relative)))
            .StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
    }

    [Fact]
    public async Task Empty_period_returns_zeroes_not_fabricated_data()
    {
        await _host.LoginAsync(Org.Rahul);
        var s = await Get($"/api/attendance/employees/{Org.Rahul.Id}/summary?start=2026-03-07&end=2026-03-08"); // weekend
        (s.GetProperty("expected_days").GetInt32(), s.GetProperty("attendance_percentage").ValueKind, s.GetProperty("net_minutes").GetInt32())
            .Should().Be((0, JsonValueKind.Null, 0));
    }

    [Fact]
    public void Legacy_session_table_still_rejects_second_open_session()
    {
        using var db = _host.Db();
        db.AttendanceSessions.Add(new AttendanceSession
        {
            EmployeeId = Org.Rahul.Id, CheckInAt = new DateTimeOffset(2026, 3, 9, 4, 0, 0, TimeSpan.Zero), Status = SessionStatus.Open, Source = SessionSource.Web,
        });
        db.SaveChanges();
        db.AttendanceSessions.Add(new AttendanceSession
        {
            EmployeeId = Org.Rahul.Id, CheckInAt = new DateTimeOffset(2026, 3, 9, 6, 0, 0, TimeSpan.Zero), Status = SessionStatus.Open, Source = SessionSource.Web,
        });
        FluentActions.Invoking(() => db.SaveChanges()).Should().Throw<DbUpdateException>();
    }

    [Fact]
    public async Task Legacy_sessions_do_not_count_as_attendance()
    {
        _host.Clock.UtcNow = TestData.NoonIst(new DateOnly(2026, 3, 13));
        TestData.Cover(_host, new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 12), Org.Rahul);
        using (var db = _host.Db())
        {
            db.AttendanceSessions.Add(new AttendanceSession
            {
                EmployeeId = Org.Rahul.Id, CheckInAt = new DateTimeOffset(2026, 3, 10, 4, 0, 0, TimeSpan.Zero),
                CheckOutAt = new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero), NetMinutes = 480, Status = SessionStatus.Completed, Source = SessionSource.Web,
            });
            db.SaveChanges();
        }

        await _host.LoginAsync(Org.Rahul);
        var day = await Get($"/api/attendance/employees/{Org.Rahul.Id}/days/2026-03-10");
        (day.GetProperty("record").GetProperty("status").GetString(), day.GetProperty("timesheet").ValueKind).Should().Be(("ABSENT", JsonValueKind.Null));
    }
}
