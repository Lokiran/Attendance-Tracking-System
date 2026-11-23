using System.Net;
using System.Text;
using Ats.Application.Scope;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Infrastructure.Database;
using Ats.Infrastructure.Persistence;
using Ats.Tests.Auth;
using Ats.Tests.Uploads;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Ats.Tests.Persistence;

/// <summary>
/// PostgreSQL behaviour (ports of backend/app/tests/postgres): the constraints the migration creates, the recursive
/// reporting-line query, scope enforcement through the API, timestamptz round trips and an all-or-nothing commit.
/// Opt-in like the other PostgreSQL tests (ATS_TEST_POSTGRES_URL).
/// </summary>
[Collection(Databases.PostgresCollection)]
public sealed class PostgresBehaviourTests
{
    private const string ExclusionViolation = "23P01";
    private const string UniqueViolation = "23505";
    private const string CheckViolation = "23514";

    private static async Task<DatabaseTarget> MigratedAsync()
    {
        var target = await Databases.EmptyPostgresAsync();
        (await new DatabaseCommands(target, TextWriter.Null).MigrateAsync()).Should().Be(0);
        return target;
    }

    private static async Task<object?> Scalar(DatabaseTarget target, string sql, params (string Name, object Value)[] args)
    {
        await using var conn = new NpgsqlConnection(target.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand(sql, conn);
        foreach (var (name, value) in args)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        return await cmd.ExecuteScalarAsync();
    }

    private static async Task<int> InsertUser(DatabaseTarget target, string code) =>
        (int)(await Scalar(target, "INSERT INTO users (employee_code, full_name, email, password_hash, role, is_active, attendance_tracked, date_joined, "
                                   + "created_at, updated_at) VALUES (@code, 'Pg User', @email, 'x', 'EMPLOYEE', true, true, '2025-01-01', now(), now()) RETURNING id",
                           ("code", code), ("email", $"{code.ToLowerInvariant()}@test.example")))!;

    private static DateTimeOffset T(int hour) => new(2026, 3, 9, hour, 0, 0, TimeSpan.Zero);

    /// <summary>A row in the legacy session table (kept with its database rules).</summary>
    private static Task<object?> Session(DatabaseTarget target, int employeeId, DateTimeOffset checkIn, DateTimeOffset? checkOut = null, string status = "COMPLETED",
                                int? netMinutes = -1) =>
        Scalar(target, "INSERT INTO attendance_sessions (employee_id, check_in_at, check_out_at, net_minutes, status, source, break_minutes, created_at, updated_at) "
                       + "VALUES (@e, @in, @out, @net, @status, 'ADMIN', 0, @in, @in)",
               ("e", employeeId), ("in", checkIn), ("out", (object?)checkOut ?? DBNull.Value),
               ("net", netMinutes == -1 ? (object?)(checkOut is { } o ? (int)(o - checkIn).TotalMinutes : null) ?? DBNull.Value : (object?)netMinutes ?? DBNull.Value),
               ("status", status));

    private static async Task<string> SqlState(Func<Task> action) =>
        (await FluentActions.Awaiting(action).Should().ThrowAsync<PostgresException>()).Which.SqlState;

    [PostgresFact]
    public async Task Migration_installs_the_extension_and_the_session_constraints()
    {
        var target = await MigratedAsync();
        (await Scalar(target, "SELECT extname FROM pg_extension WHERE extname = 'btree_gist'")).Should().Be("btree_gist");
        var employee = await InsertUser(target, "PG001");
        await Session(target, employee, T(4), T(7));
        (await SqlState(() => Session(target, employee, T(6), T(9)))).Should().Be(ExclusionViolation); // overlapping sessions
        await Session(target, employee, T(7), T(8));                       // adjacent: [4,7) [7,8)
        await Session(target, employee, T(5), T(6), status: "VOIDED");      // history rows may overlap
        var second = await InsertUser(target, "PG002");
        await Session(target, second, T(4), T(7));                         // per employee
        await Session(target, employee, T(10), status: "OPEN");
        (await SqlState(() => Session(target, employee, T(12), status: "OPEN"))).Should().Be(UniqueViolation); // one open session
        await Session(target, employee, T(1), status: "MISSING_CHECKOUT"); // outside the partial index
        (await SqlState(() => Session(target, second, T(20), T(19), netMinutes: 0))).Should().Be(CheckViolation); // check-out after check-in
    }

    [PostgresFact]
    public async Task Policy_defaults_and_checks()
    {
        var target = await MigratedAsync();
        await Scalar(target, "INSERT INTO attendance_policy (id, min_present_minutes, count_incomplete_as_present, max_session_minutes, late_grace_minutes, "
                             + "correction_window_days, manager_can_approve, updated_at) VALUES (1, 0, false, 960, 15, 30, true, now())");
        (await Scalar(target, "SELECT wfh_presence_states::text FROM attendance_policy")).Should().Be("""["Available", "Busy", "InAMeeting"]""");
        (await Scalar(target, "SELECT gap_minutes FROM attendance_policy")).Should().Be(30);
        (await SqlState(() => Scalar(target, "UPDATE attendance_policy SET half_day_minutes = full_day_minutes + 1"))).Should().Be(CheckViolation);
    }

    [PostgresFact]
    public async Task Recursive_subtree_and_a_cycle_terminates_on_postgres()
    {
        await MigratedAsync();
        using var host = new AuthHost(new Dictionary<string, string?> { ["DATABASE_URL"] = Databases.PostgresUrl });
        var org = host.Org;
        int lead, junior;
        using (var db = host.Db())
        {
            var l = new User { EmployeeCode = "TL1", FullName = "Team Lead", Email = "lead@test.example", PasswordHash = "x", Role = Role.Manager, ManagerId = org.Priya.Id, DateJoined = new DateOnly(2025, 1, 1) };
            db.Users.Add(l);
            db.SaveChanges();
            var j = new User { EmployeeCode = "JD1", FullName = "Junior Dev", Email = "junior@test.example", PasswordHash = "x", Role = Role.Employee, ManagerId = l.Id, DateJoined = new DateOnly(2025, 1, 1) };
            db.Users.Add(j);
            db.SaveChanges();
            (lead, junior) = (l.Id, j.Id);
        }

        using (var scope = host.Api.Services.CreateScope())
        {
            var scopes = scope.ServiceProvider.GetRequiredService<ScopeService>();
            (await scopes.SubtreeIdsAsync(org.Priya.Id)).Should().BeEquivalentTo([org.Rahul.Id, org.Sneha.Id, lead, junior]);
            (await scopes.SubtreeIdsAsync(org.Vikram.Id)).Should().BeEquivalentTo([org.Priya.Id, org.Anil.Id, org.Rahul.Id, org.Sneha.Id, org.Arjun.Id, lead, junior]);
            (await scopes.SubtreeIdsAsync(org.Rahul.Id)).Should().BeEmpty();
        }

        using (var db = host.Db())
        {
            // The API refuses cycles; force one in SQL to prove the recursive query still terminates.
            await db.Database.ExecuteSqlRawAsync("UPDATE users SET manager_id = {0} WHERE id = {1}", org.Arjun.Id, org.Vikram.Id);
        }

        using (var scope = host.Api.Services.CreateScope())
        {
            var ids = await scope.ServiceProvider.GetRequiredService<ScopeService>().SubtreeIdsAsync(org.Vikram.Id).WaitAsync(TimeSpan.FromSeconds(10));
            ids.Should().Contain([org.Anil.Id, org.Arjun.Id, org.Priya.Id]);
        }
    }

    [PostgresFact]
    public async Task Manager_scope_is_enforced_by_the_api_on_postgres()
    {
        await MigratedAsync();
        using var host = new AuthHost(new Dictionary<string, string?> { ["DATABASE_URL"] = Databases.PostgresUrl });
        host.Clock.UtcNow = TestData.Tuesday;
        var org = host.Org;
        const string range = "start=2026-03-01&end=2026-03-10";
        await host.LoginAsync(org.Priya);
        var rows = await WriteClient.Json(await host.Client.GetAsync(new Uri($"/api/dashboard/employees?{range}&page_size=50", UriKind.Relative)));
        rows.GetProperty("items").EnumerateArray().Select(r => r.GetProperty("employee_id").GetInt32()).Order().Should().Equal(new[] { org.Rahul.Id, org.Sneha.Id }.Order());
        (await host.Client.GetAsync(new Uri($"/api/attendance/employees/{org.Arjun.Id}/summary?{range}", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await host.Client.GetAsync(new Uri($"/api/dashboard/employees?{range}&manager_id={org.Anil.Id}", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var csv = await (await host.Client.GetAsync(new Uri($"/api/reports/team-attendance.csv?{range}", UriKind.Relative))).Content.ReadAsStringAsync();
        csv.Should().Contain("Rahul Reddy").And.NotContain("Arjun Kumar");
        (await host.Client.GetAsync(new Uri($"/api/attendance/employees/{org.Arjun.Id}/days/2026-03-09", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        await host.LoginAsync(org.Anil);
        (await host.Client.GetAsync(new Uri($"/api/attendance/employees/{org.Arjun.Id}/days/2026-03-09", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.OK);
        await host.LoginAsync(org.Vikram);
        (await WriteClient.Json(await host.Client.GetAsync(new Uri($"/api/dashboard/employees?{range}&page_size=50", UriKind.Relative)))).GetProperty("items").GetArrayLength().Should().Be(5);
        await host.LoginAsync(org.Rahul);
        (await host.Client.GetAsync(new Uri($"/api/dashboard/overview?{range}", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private static async Task<int> Preview(AuthHost host, string csrf, string csv)
    {
        using var form = new MultipartFormDataContent { { new ByteArrayContent(Encoding.UTF8.GetBytes(csv)), "file", "t.csv" } };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/admin/attendance-uploads") { Content = form };
        request.Headers.Add("X-CSRF-Token", csrf);
        return (await WriteClient.Json(await host.Client.SendAsync(request))).GetProperty("id").GetInt32();
    }

    [PostgresFact]
    public async Task Timesheet_times_round_trip_as_utc_regardless_of_the_session_time_zone()
    {
        var target = await MigratedAsync();
        using var host = new AuthHost(new Dictionary<string, string?> { ["DATABASE_URL"] = Databases.PostgresUrl });
        host.Clock.UtcNow = TestData.NoonIst(new DateOnly(2026, 11, 10));
        var csrf = await host.LoginAsync(host.Org.Admin);
        var id = await Preview(host, csrf, $"Employee ID,Date,In,Out\n{host.Org.Rahul.EmployeeCode},10/03/2026,09:15,18:00\n");
        (await host.Send(HttpMethod.Post, $"/api/admin/attendance-uploads/{id}/commit", csrf, new { })).Status.Should().Be(HttpStatusCode.OK);
        await using (var conn = new NpgsqlConnection(target.ConnectionString))
        {
            await conn.OpenAsync();
            await using (var set = new NpgsqlCommand("SET TIME ZONE 'America/New_York'", conn))
            {
                await set.ExecuteNonQueryAsync(); // the server's display zone must not matter
            }

            await using var cmd = new NpgsqlCommand("SELECT (first_in AT TIME ZONE 'UTC')::text, (last_out AT TIME ZONE 'UTC')::text FROM attendance_daily", conn);
            await using var reader = await cmd.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            (reader.GetString(0), reader.GetString(1)).Should().Be(("2026-03-10 03:45:00", "2026-03-10 12:30:00")); // 09:15 and 18:00 IST
        }

        using (var db = host.Db())
        {
            await db.Database.OpenConnectionAsync();
            await db.Database.ExecuteSqlRawAsync("SET TIME ZONE 'Pacific/Auckland'");
            var row = await db.AttendanceDaily.SingleAsync();
            (row.FirstIn, row.FirstIn!.Value.Offset).Should().Be((new DateTimeOffset(2026, 3, 10, 3, 45, 0, TimeSpan.Zero), TimeSpan.Zero));
        }

        var day = await WriteClient.Json(await host.Client.GetAsync(new Uri($"/api/attendance/employees/{host.Org.Rahul.Id}/days/2026-03-10", UriKind.Relative)));
        (day.GetProperty("record").GetProperty("net_minutes").GetInt32(), day.GetProperty("record").GetProperty("status").GetString()).Should().Be((525, "PRESENT"));
    }

    [PostgresFact]
    public async Task A_failed_commit_rolls_back_entirely()
    {
        var target = await MigratedAsync();
        using var host = new AuthHost(new Dictionary<string, string?> { ["DATABASE_URL"] = Databases.PostgresUrl });
        host.Clock.UtcNow = TestData.NoonIst(new DateOnly(2026, 11, 10));
        var csrf = await host.LoginAsync(host.Org.Admin);
        var id = await Preview(host, csrf, $"Employee ID,Date\n{host.Org.Rahul.EmployeeCode},01/10/2026\n{host.Org.Sneha.EmployeeCode},01/10/2026\n");
        // The commit's last write (the upload becoming COMMITTED) fails after the day rows were written in the same transaction.
        await Scalar(target, """
            CREATE FUNCTION fail_commit() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'storage failed half way'; END $$;
            CREATE TRIGGER fail_commit BEFORE UPDATE OF status ON attendance_uploads FOR EACH ROW
            WHEN (NEW.status = 'COMMITTED') EXECUTE FUNCTION fail_commit();
            """);
        (await host.Send(HttpMethod.Post, $"/api/admin/attendance-uploads/{id}/commit", csrf, new { })).Status.Should().Be(HttpStatusCode.InternalServerError);
        (await Scalar(target, "SELECT count(*) FROM attendance_daily")).Should().Be(0L);
        (await Scalar(target, "SELECT status FROM attendance_uploads WHERE id = @id", ("id", id))).Should().Be("PREVIEW"); // can be committed again
        await Scalar(target, "DROP TRIGGER fail_commit ON attendance_uploads; DROP FUNCTION fail_commit()");
        (await host.Send(HttpMethod.Post, $"/api/admin/attendance-uploads/{id}/commit", csrf, new { })).Status.Should().Be(HttpStatusCode.OK);
        (await Scalar(target, "SELECT count(*) FROM attendance_daily")).Should().Be(2L);
    }
}
