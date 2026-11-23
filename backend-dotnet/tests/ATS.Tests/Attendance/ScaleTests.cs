using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Ats.Application.Attendance;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Tests.Auth;
using Ats.Tests.Uploads;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Ats.Tests.Attendance;

/// <summary>
/// Scale behaviour (port of backend/app/tests/test_scale.py): dashboard queries do not grow with headcount, the shared record
/// cache returns exactly what a fresh computation returns and never serves stale numbers (also after a write by another
/// process), identical concurrent requests compute once, and the cache stays inside its budget.
/// </summary>
public sealed class ScaleTests : IDisposable
{
    private const string Range = "start=2026-03-02&end=2026-03-10&on_date=2026-03-10";
    private static readonly string[] DashboardCalls =
    [
        $"/api/dashboard/overview?{Range}", $"/api/dashboard/employees?{Range}&page_size=10",
        $"/api/dashboard/employees?{Range}&page_size=200&sort=attendance_percentage&order=desc",
        "/api/dashboard/trends?start=2026-03-02&end=2026-03-10&granularity=day", "/api/dashboard/exceptions?start=2026-03-02&end=2026-03-10&page_size=8",
        $"/api/dashboard/departments?{Range}", $"/api/dashboard/managers?{Range}",
    ];

    private readonly AuthHost _host = new();

    public ScaleTests() => _host.Clock.UtcNow = TestData.Tuesday;

    public void Dispose() => _host.Dispose();

    private RecordCache Cache => _host.Api.Services.GetRequiredService<RecordCache>();

    /// <summary>
    /// The admin (not tracked), <paramref name="managers"/> managers and <paramref name="employees"/> employees with a week of
    /// imported days each (a committed timesheet covers 2-9 March). The AuthHost org's other people are left out of the stats.
    /// </summary>
    private List<User> BuildOrg(int employees, int managers = 3)
    {
        using var db = _host.Db();
        foreach (var u in db.Users)
        {
            u.AttendanceTracked = false;
        }

        var dept = new Department { Name = "Operations" };
        db.Departments.Add(dept);
        db.SaveChanges();
        var mgrs = Enumerable.Range(0, managers).Select(i => new User
        {
            EmployeeCode = $"M{i:000}", FullName = $"Manager {i}", Email = $"manager{i}@scale.example", PasswordHash = AuthHost.PythonHash, Role = Role.Manager,
            ManagerId = _host.Org.Admin.Id, DepartmentId = dept.Id, DateJoined = new DateOnly(2025, 1, 1),
        }).ToList();
        db.Users.AddRange(mgrs);
        db.SaveChanges();
        var staff = Enumerable.Range(0, employees).Select(i => new User
        {
            EmployeeCode = $"S{i:00000}", FullName = $"Staff {i:00000}", Email = $"staff{i}@scale.example", PasswordHash = AuthHost.PythonHash, Role = Role.Employee,
            DepartmentId = dept.Id, ManagerId = mgrs[i % managers].Id, DateJoined = new DateOnly(2025, 1, 1),
        }).ToList();
        db.Users.AddRange(staff);
        db.SaveChanges();
        foreach (var u in staff)
        {
            for (var day = 2; day <= 9; day++)
            {
                var t = new DateTimeOffset(2026, 3, day, 4, 0, 0, TimeSpan.Zero).AddMinutes((u.Id * 7 + day) % 50);
                if (t.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday || (u.Id + day) % 9 == 0)
                {
                    continue;
                }

                db.AttendanceDaily.Add(Daily(u.Id, DateOnly.FromDateTime(t.UtcDateTime), t, 9 * 60 - 45));
            }
        }

        db.SaveChanges();
        TestData.Cover(_host, new DateOnly(2026, 3, 2), new DateOnly(2026, 3, 9), [.. staff, .. mgrs]);
        return mgrs;
    }

    private static AttendanceDaily Daily(int uid, DateOnly d, DateTimeOffset? firstIn, int? minutes) => new()
    {
        EmployeeId = uid, WorkDate = d, OfficeMinutes = 0, WfhMinutes = 0, Status = DailyStatus.Present, Corrected = false, Conflicts = [],
        UploadWorkMode = WorkMode.Office, UploadMinutes = minutes, FirstIn = firstIn,
    };

    private async Task<JsonElement> Get(string path)
    {
        var r = await _host.Client.GetAsync(new Uri(path, UriKind.Relative));
        r.StatusCode.Should().Be(HttpStatusCode.OK, path);
        return await WriteClient.Json(r);
    }

    private async Task<List<string>> DashboardJson()
    {
        var output = new List<string>();
        foreach (var path in DashboardCalls)
        {
            var body = await Get(path);
            output.Add(body.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Serialize(body.EnumerateObject().Where(p => p.Name != "generated_at").ToDictionary(p => p.Name, p => p.Value))
                : body.GetRawText());
        }

        return output;
    }

    /// <summary>Counts the SQL commands EF sends to this host's database while each dashboard call runs.</summary>
    private sealed class CommandCounter : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>, IDisposable
    {
        private readonly string _database;
        private readonly IDisposable _subscription;
        private readonly List<IDisposable> _listeners = [];
        private int _count;

        public CommandCounter(string database)
        {
            _database = database;
            _subscription = DiagnosticListener.AllListeners.Subscribe(this);
        }

        public int Take() => Interlocked.Exchange(ref _count, 0);

        public void OnNext(DiagnosticListener value)
        {
            if (value.Name == DbLoggerCategory.Name)
            {
                lock (_listeners)
                {
                    _listeners.Add(value.Subscribe(this));
                }
            }
        }

        public void OnNext(KeyValuePair<string, object?> value)
        {
            if (value.Key == RelationalEventId.CommandExecuting.Name && value.Value is CommandEventData data
                && data.Command.Connection?.DataSource?.Contains(_database, StringComparison.OrdinalIgnoreCase) == true)
            {
                Interlocked.Increment(ref _count);
            }
        }

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }

        public void Dispose()
        {
            _subscription.Dispose();
            lock (_listeners)
            {
                _listeners.ForEach(l => l.Dispose());
            }
        }
    }

    private async Task<List<int>> CountStatements()
    {
        string database;
        using (var db = _host.Db())
        {
            database = Path.GetFileName(db.Database.GetDbConnection().DataSource);
        }

        using var counter = new CommandCounter(database);
        var counts = new List<int>();
        foreach (var path in DashboardCalls)
        {
            counter.Take();
            await Get(path);
            counts.Add(counter.Take());
        }

        return counts;
    }

    [Fact]
    public async Task Dashboard_queries_do_not_grow_with_headcount()
    {
        // 30 people, not Python's 10: both counts then run with the record cache (scopes of 25 or more), whose two
        // fingerprint queries are part of every cached computation, so the comparison is per-employee queries only.
        var mgrs = BuildOrg(30);
        await _host.LoginAsync(_host.Org.Admin);
        Cache.Clear();
        var before = await CountStatements();
        using (var db = _host.Db())
        {
            db.Users.AddRange(Enumerable.Range(0, 60).Select(i => new User
            {
                EmployeeCode = $"X{i:00000}", FullName = $"Extra {i:00000}", Email = $"extra{i}@scale.example", PasswordHash = AuthHost.PythonHash,
                Role = Role.Employee, ManagerId = mgrs[1].Id, DateJoined = new DateOnly(2025, 1, 1),
            }));
            db.SaveChanges();
        }

        Cache.Clear();
        (await CountStatements()).Should().Equal(before, "a dashboard endpoint issues per-employee queries");
    }

    [Fact]
    public async Task Cached_results_equal_fresh_results()
    {
        BuildOrg(60);
        await _host.LoginAsync(_host.Org.Admin);
        var fresh = new List<string>();
        foreach (var path in DashboardCalls)
        {
            Cache.Clear(); // nothing cached: every call computes
            var body = await Get(path);
            fresh.Add(body.ValueKind == JsonValueKind.Object
                ? JsonSerializer.Serialize(body.EnumerateObject().Where(p => p.Name != "generated_at").ToDictionary(p => p.Name, p => p.Value))
                : body.GetRawText());
        }

        Cache.Clear();
        var first = await DashboardJson();  // fills the cache
        var second = await DashboardJson(); // served from the cache
        first.Should().Equal(fresh);
        second.Should().Equal(fresh);
    }

    [Fact]
    public async Task Imported_day_shows_up_immediately()
    {
        BuildOrg(30);
        int staffId;
        using (var db = _host.Db())
        {
            staffId = db.Users.Single(u => u.Email == "staff1@scale.example").Id;
        }

        await _host.LoginAsync(_host.Org.Admin);
        var before = (await Get($"/api/dashboard/overview?{Range}")).GetProperty("snapshot");
        using (var db = _host.Db())
        {
            // Same frozen clock: only the in-process write version can tell the data changed.
            db.AttendanceDaily.Add(Daily(staffId, new DateOnly(2026, 3, 10), null, null));
            db.SaveChanges();
        }

        var after = (await Get($"/api/dashboard/overview?{Range}")).GetProperty("snapshot");
        after.GetProperty("present").GetInt32().Should().Be(before.GetProperty("present").GetInt32() + 1);
        after.GetProperty("no_timesheet").GetInt32().Should().Be(before.GetProperty("no_timesheet").GetInt32() - 1);
    }

    [Fact]
    public async Task Update_with_unchanged_timestamp_shows_up()
    {
        BuildOrg(30);
        await _host.LoginAsync(_host.Org.Admin);
        Cache.Clear();
        var before = (await Get($"/api/dashboard/overview?{Range}")).GetProperty("period").GetProperty("net_minutes").GetInt32();
        using (var db = _host.Db())
        {
            var row = db.AttendanceDaily.OrderBy(a => a.Id).First();
            row.UploadMinutes += 30; // the frozen clock keeps updated_at: the fingerprint cannot see it, the write version must
            db.SaveChanges();
        }

        (await Get($"/api/dashboard/overview?{Range}")).GetProperty("period").GetProperty("net_minutes").GetInt32().Should().Be(before + 30);
    }

    [Fact]
    public async Task Write_from_another_process_is_picked_up()
    {
        BuildOrg(30);
        await _host.LoginAsync(_host.Org.Admin);
        var before = (await Get($"/api/dashboard/overview?{Range}")).GetProperty("period").GetProperty("net_minutes").GetInt32();
        var version = Cache.WriteVersion;
        using (var db = _host.Db())
        {
            // A raw UPDATE, as another server process would do: it bypasses this process's write tracking.
            db.Database.ExecuteSqlRaw("UPDATE attendance_daily SET upload_minutes = upload_minutes + 60, updated_at = '2030-01-01 00:00:00.000000' "
                                      + "WHERE id = (SELECT min(id) FROM attendance_daily)");
        }

        Cache.WriteVersion.Should().Be(version); // this process did not notice the write...
        (await Get($"/api/dashboard/overview?{Range}")).GetProperty("period").GetProperty("net_minutes").GetInt32().Should().Be(before + 60); // ...but the dashboard is right
    }

    [Fact]
    public async Task Schedule_change_invalidates()
    {
        BuildOrg(30);
        await _host.LoginAsync(_host.Org.Admin);
        var before = (await Get($"/api/dashboard/overview?{Range}")).GetProperty("period").GetProperty("required_minutes").GetInt32();
        using (var db = _host.Db())
        {
            db.Database.ExecuteSqlRaw("UPDATE work_schedules SET required_minutes = 420");
        }

        (await Get($"/api/dashboard/overview?{Range}")).GetProperty("period").GetProperty("required_minutes").GetInt32().Should().Be(before * 420 / 480);
    }

    [Fact]
    public async Task Attendance_bands_count_everyone_not_a_sample()
    {
        BuildOrg(260);
        await _host.LoginAsync(_host.Org.Admin);
        var o = await Get($"/api/dashboard/overview?{Range}");
        o.GetProperty("attendance_bands").EnumerateArray().Select(b => b.GetProperty("label").GetString()).Should().Equal("<70%", "70–80%", "80–90%", "90–95%", "95–100%");
        o.GetProperty("attendance_bands").EnumerateArray().Sum(b => b.GetProperty("count").GetInt32()).Should().Be(o.GetProperty("headcount").GetInt32()).And.Be(263);
    }

    [Fact]
    public async Task People_search_is_limited_and_scoped()
    {
        var mgrs = BuildOrg(300);
        await _host.LoginAsync(_host.Org.Admin);
        var r = (await Get("/api/team/members?search=staff%20001&limit=5")).EnumerateArray().Select(p => p.GetProperty("full_name").GetString()!).ToList();
        r.Should().HaveCount(5).And.OnlyContain(n => n.Contains("Staff 001", StringComparison.Ordinal)).And.BeInAscendingOrder(StringComparer.Ordinal);
        (await Get("/api/team/members?search=s00042&limit=20")).EnumerateArray().Select(p => p.GetProperty("employee_code").GetString()).Should().Equal("S00042");
        (await _host.Client.GetAsync(new Uri("/api/team/members?limit=500", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);

        await _host.LoginAsync(mgrs[0]); // a manager finds only people in their subtree, and gets 404 for anyone else
        var mine = await Get("/api/team/members?search=staff&limit=200");
        mine.GetArrayLength().Should().BePositive();
        mine.EnumerateArray().Should().OnlyContain(p => p.GetProperty("manager_id").GetInt32() == mgrs[0].Id);
        int otherId;
        using (var db = _host.Db())
        {
            otherId = db.Users.First(u => u.ManagerId == mgrs[1].Id).Id;
        }

        (await _host.Client.GetAsync(new Uri($"/api/team/members/{otherId}", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var own = mine[0];
        (await Get($"/api/team/members/{own.GetProperty("id").GetInt32()}")).GetProperty("full_name").GetString().Should().Be(own.GetProperty("full_name").GetString());
        (await _host.Client.GetAsync(new Uri("/api/team/members/999999", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Identical_concurrent_requests_compute_once()
    {
        var cache = new RecordCache();
        var calls = 0;
        var gate = new TaskCompletionSource();
        async Task<string> Compute()
        {
            Interlocked.Increment(ref calls);
            await gate.Task.WaitAsync(TimeSpan.FromSeconds(2));
            return "42";
        }

        var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(() => cache.GetOrComputeAsync("k", 1, Compute))).ToList();
        await Task.Delay(200);
        gate.SetResult();
        var results = await Task.WhenAll(tasks);
        calls.Should().Be(1);
        results.Should().OnlyContain(v => v == "42").And.HaveCount(6);
    }

    [Fact]
    public async Task Cache_stays_within_budget()
    {
        var cache = new RecordCache { Budget = 100 };
        for (var i = 0; i < 10; i++)
        {
            var value = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await cache.GetOrComputeAsync($"entry{i}", 30, () => Task.FromResult(value));
        }

        cache.Usage.Total.Should().BeLessThanOrEqualTo(100);
        cache.Usage.Entries.Should().Be(3);
        (await cache.GetOrComputeAsync("entry9", 30, () => Task.FromResult("recomputed"))).Should().Be("9"); // the newest entries are kept
        (await cache.GetOrComputeAsync("huge", 1000, () => Task.FromResult("big"))).Should().Be("big");     // returned but not kept
        cache.Contains("huge").Should().BeFalse();
    }

    [Fact]
    public async Task Failed_computation_does_not_block_others()
    {
        var cache = new RecordCache();
        await FluentActions.Awaiting(() => cache.GetOrComputeAsync<string>("bad", 1, () => throw new InvalidOperationException("database unavailable")))
            .Should().ThrowAsync<InvalidOperationException>();
        (await cache.GetOrComputeAsync("bad", 1, () => Task.FromResult("ok"))).Should().Be("ok");
    }
}
