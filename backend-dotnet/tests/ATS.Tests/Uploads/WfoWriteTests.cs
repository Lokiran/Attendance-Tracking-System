using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Infrastructure.Persistence;
using Ats.Tests.Auth;
using Ats.Tests.Persistence;
using FluentAssertions;

namespace Ats.Tests.Uploads;

/// <summary>Shared HTTP helpers for the write tests (CSRF header, multipart upload, JSON body).</summary>
internal static class WriteClient
{
    public static async Task<JsonElement> Json(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> Send(this AuthHost host, HttpMethod method, string path, string? csrf,
                                                                            object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (csrf is not null)
        {
            request.Headers.Add("X-CSRF-Token", csrf);
        }

        var r = await host.Client.SendAsync(request);
        return (r.StatusCode, await Json(r));
    }

    public static async Task<(HttpStatusCode Status, JsonElement Body)> Upload(this AuthHost host, string csrf, string name, byte[] content,
                                                                              string mime = "text/csv")
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(file, "file", name);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/attendance-uploads") { Content = form };
        request.Headers.Add("X-CSRF-Token", csrf);
        var r = await host.Client.SendAsync(request);
        return (r.StatusCode, await Json(r));
    }

    public static JsonElement Day(JsonElement body, string date) =>
        body.GetProperty("days").EnumerateArray().First(d => d.GetProperty("date").GetString() == date);
}

/// <summary>
/// Day overrides and the WFO policy (ports of the write tests in backend/app/tests/test_wfo_api.py). Clock: Tuesday 10 March
/// 2026, 12:00 IST; org: Vikram -> Priya -> Rahul, Sneha; Vikram -> Anil -> Arjun.
/// </summary>
public sealed class WfoWriteTests : IDisposable
{
    private readonly AuthHost _host = new();

    public WfoWriteTests() => _host.Clock.UtcNow = new DateTimeOffset(2026, 3, 10, 6, 30, 0, TimeSpan.Zero);

    public void Dispose() => _host.Dispose();

    /// <summary>An imported timesheet day: hours, and the work mode column (null: the file had no mode for it).</summary>
    private void Worked(User user, DateOnly d, double hours = 8.5, WorkMode? location = null)
    {
        using var db = _host.Db();
        var start = new DateTimeOffset(d.Year, d.Month, d.Day, 4, 0, 0, TimeSpan.Zero); // 09:30 IST
        db.AttendanceDaily.Add(new AttendanceDaily
        {
            EmployeeId = user.Id, WorkDate = d, OfficeMinutes = 0, WfhMinutes = 0, Corrected = false, Conflicts = [], Status = DailyStatus.Absent,
            UploadWorkMode = location, UploadMinutes = (int)(hours * 60), FirstIn = start, LastOut = start.AddMinutes((int)(hours * 60)),
        });
        db.SaveChanges();
    }

    private async Task<JsonElement> Month(User u) =>
        await WriteClient.Json(await _host.Client.GetAsync(new Uri($"/api/team/wfo/employees/{u.Id}?month=2026-03", UriKind.Relative)));

    [Fact]
    public async Task Admin_override_wins_and_is_audited()
    {
        var org = _host.Org;
        Worked(org.Rahul, new DateOnly(2026, 3, 5)); // inferred WFH
        var url = $"/api/admin/attendance/{org.Rahul.Id}/2026-03-05/work-mode";
        (await _host.Send(HttpMethod.Put, url, await _host.LoginAsync(org.Priya), new { work_mode = "OFFICE" })).Status.Should().Be(HttpStatusCode.Forbidden);
        var csrf = await _host.LoginAsync(org.Admin);
        var (status, body) = await _host.Send(HttpMethod.Put, url, csrf, new { work_mode = "OFFICE", note = "Client visit, badge missing" });
        status.Should().Be(HttpStatusCode.OK, body.ToString());
        (body.GetProperty("wfo_source").GetString(), body.GetProperty("overridden").GetBoolean()).Should().Be(("MANUAL_OVERRIDE", true));
        var d = WriteClient.Day(await Month(org.Rahul), "2026-03-05");
        (d.GetProperty("work_mode").GetString(), d.GetProperty("wfo_source").GetString(), d.GetProperty("overridden").GetBoolean())
            .Should().Be(("OFFICE", "MANUAL_OVERRIDE", true));
        (d.GetProperty("override_note").GetString(), d.GetProperty("is_wfo_day").GetBoolean()).Should().Be(("Client visit, badge missing", true));
        using (var db = _host.Db())
        {
            var entry = db.AuditLogs.Single(a => a.Action == "WORK_MODE_OVERRIDDEN");
            (entry.ActorUserId, entry.EntityId).Should().Be(((int?)org.Admin.Id, $"{org.Rahul.Id}:2026-03-05"));
            (entry.BeforeValues!["work_mode"]!.GetValue<string>(), entry.AfterValues!["work_mode"]!.GetValue<string>()).Should().Be(("WFH", "OFFICE"));
        }

        (await _host.Send(HttpMethod.Delete, url, csrf)).Status.Should().Be(HttpStatusCode.OK);
        d = WriteClient.Day(await Month(org.Rahul), "2026-03-05");
        (d.GetProperty("work_mode").GetString(), d.GetProperty("wfo_source").GetString()).Should().Be(("WFH", "INFERRED"));
        using (var db = _host.Db())
        {
            db.AuditLogs.Count(a => a.Action == "WORK_MODE_OVERRIDE_CLEARED").Should().Be(1);
        }

        (await _host.Send(HttpMethod.Delete, url, csrf)).Status.Should().Be(HttpStatusCode.NotFound);
        (await _host.Send(HttpMethod.Put, url, csrf, new { work_mode = "PARTY" })).Status.Should().Be(HttpStatusCode.UnprocessableEntity);
        var (early, problem) = await _host.Send(HttpMethod.Put, $"/api/admin/attendance/{org.Rahul.Id}/2024-01-01/work-mode", csrf, new { work_mode = "LEAVE" });
        early.Should().Be(HttpStatusCode.UnprocessableEntity); // before joining
        problem.GetProperty("error").GetProperty("message").GetString().Should().Be("That date is before the employee joined");
        (await _host.Send(HttpMethod.Put, "/api/admin/attendance/999999/2026-03-05/work-mode", csrf, new { work_mode = "WFH" })).Status
            .Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Leave_can_only_come_from_an_override_or_import()
    {
        var org = _host.Org;
        var csrf = await _host.LoginAsync(org.Admin);
        await _host.Send(HttpMethod.Put, $"/api/admin/attendance/{org.Rahul.Id}/2026-03-03/work-mode", csrf, new { work_mode = "LEAVE" });
        var body = await Month(org.Rahul);
        WriteClient.Day(body, "2026-03-03").GetProperty("work_mode").GetString().Should().Be("LEAVE");
        body.GetProperty("summary").GetProperty("leave_days").GetInt32().Should().Be(1);
        body.GetProperty("summary").GetProperty("working_days").GetInt32().Should().Be(5); // 2-6 and 9 March minus the leave day
    }

    [Fact]
    public async Task Policy_changes_what_counts_as_a_wfo_day()
    {
        var org = _host.Org;
        Worked(org.Rahul, new DateOnly(2026, 3, 9), hours: 3, location: WorkMode.Office);
        await _host.LoginAsync(org.Priya);
        (await _host.Client.GetAsync(new Uri("/api/admin/wfo/policy", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var csrf = await _host.LoginAsync(org.Admin);
        var (bad, problem) = await _host.Send(HttpMethod.Put, "/api/admin/wfo/policy", csrf, new { min_office_minutes = 2000 });
        bad.Should().Be(HttpStatusCode.UnprocessableEntity);
        problem.GetProperty("error").GetProperty("message").GetString().Should().Be("min_office_minutes: Input should be less than or equal to 1440");
        var (_, body) = await _host.Send(HttpMethod.Put, "/api/admin/wfo/policy", csrf, new { min_office_minutes = 120, min_days_per_week = 2 });
        body.GetRawText().Should().Be("""{"min_days_per_week":2,"min_days_per_month":12,"min_office_minutes":120}""");
        using (var db = _host.Db())
        {
            db.AuditLogs.Count(a => a.Action == "WFO_POLICY_UPDATED").Should().Be(1);
        }

        (await _host.Send(HttpMethod.Put, "/api/admin/wfo/policy", csrf, new { min_days_per_week = 2 })).Status.Should().Be(HttpStatusCode.OK);
        using (var db = _host.Db())
        {
            db.AuditLogs.Count(a => a.Action == "WFO_POLICY_UPDATED").Should().Be(1); // nothing changed: not audited again
        }

        WriteClient.Day(await Month(org.Rahul), "2026-03-09").GetProperty("is_wfo_day").GetBoolean().Should().BeTrue(); // 3 h meets 2 h
    }
}

/// <summary>The upload lifecycle on PostgreSQL (row lock, transactions, JSON columns), opt-in like the other PostgreSQL tests.</summary>
[Collection(Databases.PostgresCollection)]
public sealed class WfoWritePostgresTests
{
    [PostgresFact]
    public async Task Upload_lifecycle_and_overrides_on_postgres()
    {
        var target = await Databases.EmptyPostgresAsync();
        (await new DatabaseCommands(target, TextWriter.Null).MigrateAsync()).Should().Be(0);
        using var host = new AuthHost(new Dictionary<string, string?> { ["DATABASE_URL"] = Databases.PostgresUrl });
        host.Clock.UtcNow = new DateTimeOffset(2026, 11, 10, 6, 30, 0, TimeSpan.Zero);
        var csrf = await host.LoginAsync(host.Org.Admin);
        var csv = "Employee,Date,Mode,Hours\nE005,05/10/2026,WFO,8\nE006,06/10/2026,Remote,7.5\nNEW-1,07/10/2026,WFO,6\n"u8.ToArray();
        var (status, up) = await host.Upload(csrf, "pg.csv", csv);
        status.Should().Be(HttpStatusCode.Created, up.ToString());
        var id = up.GetProperty("id").GetInt32();
        up.GetProperty("summary").GetProperty("unknown_employees")[0].GetProperty("employee_ref").GetString().Should().Be("NEW-1");
        (status, var added) = await host.Send(HttpMethod.Post, $"/api/admin/attendance-uploads/{id}/create-employees", csrf, new { });
        status.Should().Be(HttpStatusCode.OK, added.ToString());
        added.GetProperty("error_count").GetInt32().Should().Be(0);
        (status, var done) = await host.Send(HttpMethod.Post, $"/api/admin/attendance-uploads/{id}/commit", csrf, new { overwrite_overrides = false });
        status.Should().Be(HttpStatusCode.OK, done.ToString());
        done.GetProperty("commit_summary").GetProperty("applied_days").GetInt32().Should().Be(3);
        (await host.Send(HttpMethod.Post, $"/api/admin/attendance-uploads/{id}/commit", csrf, new { })).Status.Should().Be(HttpStatusCode.Conflict);
        var rahul = host.Org.Rahul.Id;
        (status, var ov) = await host.Send(HttpMethod.Put, $"/api/admin/attendance/{rahul}/2026-10-05/work-mode", csrf, new { work_mode = "WFH" });
        status.Should().Be(HttpStatusCode.OK, ov.ToString());
        ov.GetProperty("wfo_source").GetString().Should().Be("MANUAL_OVERRIDE");
        var (_, again) = await host.Upload(csrf, "pg.csv", csv);
        again.GetProperty("summary").GetProperty("protected_overrides").GetInt32().Should().Be(1);
        (status, done) = await host.Send(HttpMethod.Post, $"/api/admin/attendance-uploads/{again.GetProperty("id").GetInt32()}/commit", csrf,
                                         new { overwrite_overrides = true });
        status.Should().Be(HttpStatusCode.OK, done.ToString());
        done.GetProperty("commit_summary").GetProperty("overrides_overwritten").GetInt32().Should().Be(1);
        using var db = host.Db();
        db.AttendanceDaily.Count().Should().Be(3);
        db.Users.Single(u => u.EmployeeCode == "NEW-1").Email.Should().Be("new.1@imported.example.com");
    }
}
