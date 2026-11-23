using System.Net;
using Ats.Infrastructure.Persistence;
using Ats.Tests.Auth;
using Ats.Tests.Persistence;
using Ats.Tests.Uploads;
using FluentAssertions;

namespace Ats.Tests.Admin;

/// <summary>The administration endpoints on PostgreSQL (lower() / LIKE searches, replace() in the device search, the audit join).</summary>
[Collection(Databases.PostgresCollection)]
public sealed class AdminPostgresTests
{
    [PostgresFact]
    public async Task Admin_endpoints_on_postgres()
    {
        var target = await Databases.EmptyPostgresAsync();
        (await new DatabaseCommands(target, TextWriter.Null).MigrateAsync()).Should().Be(0);
        using var host = new AuthHost(new Dictionary<string, string?> { ["DATABASE_URL"] = Databases.PostgresUrl });
        var csrf = await host.LoginAsync(host.Org.Admin);
        var (status, user) = await host.Send(HttpMethod.Post, "/api/admin/users", csrf, new
        {
            employee_code = "pg 01", full_name = "Pg Person", email = "PG.Person@test.example", password = "Welcome12345", manager_id = host.Org.Priya.Id,
        });
        status.Should().Be(HttpStatusCode.Created, user.ToString());
        (await host.Send(HttpMethod.Post, "/api/admin/users", csrf, new { employee_code = "PG 01", full_name = "Other", email = "o@test.example", password = "Welcome12345" }))
            .Status.Should().Be(HttpStatusCode.Conflict);
        var id = user.GetProperty("id").GetInt32();
        var (_, patched) = await host.Send(HttpMethod.Patch, $"/api/admin/users/{id}", csrf, new { manager_id = host.Org.Anil.Id, job_title = "Analyst" });
        patched.GetProperty("manager").GetProperty("full_name").GetString().Should().Be("Anil Kumar");
        var list = await WriteClient.Json(await host.Client.GetAsync(new Uri("/api/admin/users?search=PG%20PERSON", UriKind.Relative)));
        list.GetProperty("total").GetInt32().Should().Be(1);
        (await host.Send(HttpMethod.Post, "/api/admin/departments", csrf, new { name = "Finance" })).Status.Should().Be(HttpStatusCode.Created);
        (await host.Send(HttpMethod.Post, "/api/admin/departments", csrf, new { name = "FINANCE" })).Status.Should().Be(HttpStatusCode.Conflict);
        (await host.Send(HttpMethod.Post, "/api/admin/schedules", csrf, new
        {
            name = "Pg shift", employee_id = id, start_time = "10:00", end_time = "18:00", required_minutes = 420, working_days = new[] { 1, 2, 3 },
            effective_from = "2026-01-01",
        })).Status.Should().Be(HttpStatusCode.Created);
        (await host.Send(HttpMethod.Post, "/api/admin/devices", csrf, new { serial_number = "pg-sn-1", mac_address = "00:1B:63:00:00:09", user_id = id }))
            .Status.Should().Be(HttpStatusCode.Created);
        var devices = await WriteClient.Json(await host.Client.GetAsync(new Uri("/api/admin/devices?search=001b63", UriKind.Relative)));
        devices.GetProperty("total").GetInt32().Should().Be(1);
        var audit = await WriteClient.Json(await host.Client.GetAsync(new Uri("/api/admin/audit-logs?search=admin%20user&start=2020-01-01&end=2030-12-31", UriKind.Relative)));
        audit.GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(5);
        audit.GetProperty("items")[0].GetProperty("actor_name").GetString().Should().Be("Admin User");
        var (_, settings) = await host.Send(HttpMethod.Put, "/api/admin/auto-attendance/settings", csrf, new { wfh_presence_states = new[] { "Busy" } });
        settings.GetProperty("wfh_presence_states").GetRawText().Should().Be("""["Busy"]""");
    }
}
