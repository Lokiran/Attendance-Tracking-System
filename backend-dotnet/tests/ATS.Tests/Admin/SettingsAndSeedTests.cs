using System.Net;
using System.Text.RegularExpressions;
using Ats.Application.Seed;
using Ats.Domain;
using Ats.Tests.Auth;
using Ats.Tests.Uploads;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ats.Tests.Admin;

/// <summary>Automatic-attendance settings and the demo seed (port of backend/app/tests/test_auto_settings.py).</summary>
public sealed partial class SettingsAndSeedTests : IDisposable
{
    private const string Url = "/api/admin/auto-attendance/settings";
    private readonly AuthHost _host = new();

    public void Dispose() => _host.Dispose();

    [Fact]
    public async Task Only_hr_admin_can_read_settings()
    {
        (await _host.Client.GetAsync(new Uri(Url, UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await _host.LoginAsync(_host.Org.Rahul);
        (await _host.Client.GetAsync(new Uri(Url, UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _host.LoginAsync(_host.Org.Priya);
        (await _host.Client.GetAsync(new Uri(Url, UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await _host.LoginAsync(_host.Org.Admin);
        var body = await WriteClient.Json(await _host.Client.GetAsync(new Uri(Url, UriKind.Relative)));
        var expected = """{"auto_attendance_enabled":false,"auto_shadow_mode":true,"gap_minutes":30,"full_day_minutes":480,"half_day_minutes":240,"wifi_poll_minutes":3,"presence_poll_minutes":5,"no_data_alert_minutes":15,"raw_retention_days":90,"wfh_presence_states":["Available","Busy","InAMeeting"]""";
        body.GetRawText().Should().StartWith(expected);
    }

    [Fact]
    public async Task Hr_admin_updates_settings_and_it_is_audited()
    {
        var csrf = await _host.LoginAsync(_host.Org.Admin);
        var (status, body) = await _host.Send(HttpMethod.Put, Url, csrf, new { gap_minutes = 20, wfh_presence_states = new[] { " Available ", "Busy", "Busy" } });
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (body.GetProperty("gap_minutes").GetInt32(), body.GetProperty("full_day_minutes").GetInt32()).Should().Be((20, 480));
        body.GetProperty("wfh_presence_states").GetRawText().Should().Be("""["Available","Busy"]""");
        using var db = _host.Db();
        var entry = db.AuditLogs.Single(a => a.Action == "AUTO_ATTENDANCE_SETTINGS_UPDATED");
        (entry.BeforeValues!["gap_minutes"]!.GetValue<int>(), entry.AfterValues!["gap_minutes"]!.GetValue<int>(), entry.ActorUserId)
            .Should().Be((30, 20, (int?)_host.Org.Admin.Id));
        db.AttendancePolicies.Single().WfhPresenceStates.Should().Equal("Available", "Busy");
    }

    [Fact]
    public async Task Settings_validation()
    {
        var csrf = await _host.LoginAsync(_host.Org.Admin);
        (await _host.Send(HttpMethod.Put, Url, csrf, new { half_day_minutes = 500 })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await _host.Send(HttpMethod.Put, Url, csrf, new { full_day_minutes = 200 })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await _host.Send(HttpMethod.Put, Url, csrf, new { full_day_minutes = 420, half_day_minutes = 210 })).Status.Should().Be(HttpStatusCode.OK);
        object[] bad =
        [
            new { gap_minutes = 0 }, new { wifi_poll_minutes = 61 }, new { raw_retention_days = 1 }, new { no_data_alert_minutes = 2 },
            new { wfh_presence_states = Array.Empty<string>() }, new { wfh_presence_states = new[] { new string('x', 40) } },
        ];
        foreach (var body in bad)
        {
            (await _host.Send(HttpMethod.Put, Url, csrf, body)).Status.Should().Be(HttpStatusCode.UnprocessableEntity, body.ToString());
        }

        var (_, problem) = await _host.Send(HttpMethod.Put, Url, csrf, new { wfh_presence_states = new[] { " ", "" } });
        problem.GetProperty("error").GetProperty("message").GetString().Should()
            .Be("wfh_presence_states: Value error, Give 1-20 Teams presence values of at most 32 characters");
    }

    [Fact]
    public async Task Settings_changes_need_csrf_and_hr_admin()
    {
        await _host.LoginAsync(_host.Org.Admin);
        (await _host.Send(HttpMethod.Put, Url, null, new { gap_minutes = 25 })).Status.Should().Be(HttpStatusCode.Forbidden);
        var csrf = await _host.LoginAsync(_host.Org.Priya);
        (await _host.Send(HttpMethod.Put, Url, csrf, new { gap_minutes = 25 })).Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Unchanged_update_writes_no_audit_entry()
    {
        var csrf = await _host.LoginAsync(_host.Org.Admin);
        (await _host.Send(HttpMethod.Put, Url, csrf, new { gap_minutes = 30, wfh_presence_states = new[] { "Available", "Busy", "InAMeeting" } }))
            .Status.Should().Be(HttpStatusCode.OK);
        using var db = _host.Db();
        db.AuditLogs.Count(a => a.Action == "AUTO_ATTENDANCE_SETTINGS_UPDATED").Should().Be(0);
    }

    // ------------------------------------------------------------------ seed

    [Fact]
    public void Py_random_matches_cpython()
    {
        // Values printed by CPython 3.14 for random.Random("nexa-EMP001-2026-10").
        var r = new PyRandom("nexa-EMP001-2026-10");
        new[] { r.Random(), r.Random(), r.Random() }.Should().Equal(0.5257213538028371, 0.40612303898781266, 0.06690330888335694);
        r.Gauss(4, 14).Should().Be(15.414149168179799);
        r.Gauss(20, 25).Should().Be(24.897685647923105);
        r.Choice([30, 45, 60]).Should().Be(60);
        r.Choice(Enumerable.Range(0, 100).ToList()).Should().Be(56);
        DemoSeed.DemoIdentities("EMP001").Should().Be(("02:a7:5e:0c:0e:ed", "2b68e046-3c27-5d97-8715-a3ebb3fc6e51"));
    }

    [Fact]
    public async Task Seed_creates_ten_employees_with_identities_settings_and_holidays()
    {
        using var host = new AuthHost();
        using (var db = host.Db())
        {
            // A fresh organisation: the seed brings its own people, departments and schedules.
            db.Users.ExecuteDelete();
            db.Departments.ExecuteDelete();
            db.WorkSchedules.ExecuteDelete();
        }

        host.Clock.UtcNow = new DateTimeOffset(2026, 3, 10, 6, 30, 0, TimeSpan.Zero);
        var env = new Dictionary<string, string?>
        {
            ["SEED_ADMIN_EMAIL"] = "admin@nexa.example", ["SEED_ADMIN_PASSWORD"] = "AdminPassw0rd!", ["SEED_DEMO_PASSWORD"] = "DemoPassw0rd!",
        };
        async Task<DemoSeed.Result> Run()
        {
            using var scope = host.Api.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<DemoSeed>().RunAsync(k => env.GetValueOrDefault(k));
        }

        var result = await Run();
        result.ToString().Should().Be("{'users': 15, 'timesheets_imported': 4, 'today': '2026-03-10'}");
        using (var db = host.Db())
        {
            db.Users.Count(u => u.Role == Role.Employee).Should().Be(10);
            var tracked = db.Users.Where(u => u.AttendanceTracked).ToList();
            tracked.Should().OnlyContain(u => MacPattern().IsMatch(u.WifiIdentity!) && GuidPattern().IsMatch(u.GraphUserId!));
            tracked.Select(u => u.WifiIdentity).Distinct().Count().Should().Be(tracked.Count);
            var admin = db.Users.Single(u => u.Role == Role.Admin);
            (admin.WifiIdentity, admin.GraphUserId).Should().Be(((string?)null, (string?)null));
            db.Holidays.Count().Should().BeGreaterThanOrEqualTo(3);
            db.AttendancePolicies.Single().FullDayMinutes.Should().Be(480);
            db.AttendanceUploads.Count(u => u.Status == UploadStatus.Committed).Should().Be(4); // December to March (75 days back)
            var rahul = db.Users.Single(u => u.EmployeeCode == "EMP001");
            rahul.WifiIdentity = "aa:bb:cc:dd:ee:ff";
            db.SaveChanges();
        }

        (await Run()).TimesheetsImported.Should().Be(0); // months already imported are skipped
        using (var db = host.Db())
        {
            db.Users.Single(u => u.EmployeeCode == "EMP001").WifiIdentity.Should().Be("aa:bb:cc:dd:ee:ff"); // never overwritten
            db.Users.Count(u => u.Role == Role.Employee).Should().Be(10);
        }

        env["SEED_DEMO_PASSWORD"] = "short";
        await FluentActions.Awaiting(Run).Should().ThrowAsync<Ats.Application.Common.ValidationFailedException>();
    }

    [GeneratedRegex("^02:a7:5e(:[0-9a-f]{2}){3}$")]
    private static partial Regex MacPattern();

    [GeneratedRegex("^[0-9a-f-]{36}$")]
    private static partial Regex GuidPattern();
}
