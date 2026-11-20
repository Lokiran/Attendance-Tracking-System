using System.Text.Json.Nodes;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Infrastructure.Database;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Ats.Tests.Persistence;

/// <summary>.NET reads what Python wrote, and writes what Python writes.</summary>
public sealed class DataTests
{
    private static readonly DateTimeOffset T = new DateTimeOffset(2026, 10, 5, 4, 52, 13, TimeSpan.Zero).AddTicks(1234560);
    private static readonly DateTimeOffset T2 = new(2026, 10, 5, 11, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reads_every_table_written_by_the_python_backend()
    {
        await using var db = Databases.Context(Databases.FixtureCopy());

        var users = await db.Users.Include(u => u.Department).Include(u => u.Manager).OrderBy(u => u.Id).ToListAsync();
        users.Should().HaveCount(2);
        var (boss, emp) = (users[0], users[1]);
        boss.Role.Should().Be(Role.Manager);
        boss.CreatedAt.Should().Be(T);
        boss.UpdatedAt.Should().Be(T2);
        boss.DateJoined.Should().Be(new DateOnly(2024, 1, 15));
        emp.EmployeeCode.Should().Be("Intern - 90001");
        emp.Manager!.FullName.Should().Be("Asha Rao");
        emp.Department!.Name.Should().Be("Platform Engineering");
        emp.AttendanceTracked.Should().BeFalse();
        emp.GraphUserId.Should().Be("0f8fad5b-d9cb-469f-a165-70867728950e");
        (await db.Departments.SingleAsync()).DepartmentHeadId.Should().Be(boss.Id);

        var schedules = await db.WorkSchedules.OrderBy(s => s.Id).ToListAsync();
        schedules[0].WorkingDays.Should().Equal(1, 2, 3, 4, 5);
        schedules[0].StartTime.Should().Be(new TimeOnly(9, 30));
        schedules[1].StartTime.Should().Be(new TimeOnly(22, 0, 0, 500));
        schedules[1].EndTime.Should().Be(new TimeOnly(6, 0));
        schedules[1].EffectiveTo.Should().Be(new DateOnly(2026, 12, 31));

        (await db.Holidays.CountAsync()).Should().Be(2);
        var policy = await db.AttendancePolicies.SingleAsync();
        policy.WfhPresenceStates.Should().Equal("Available", "Busy");
        policy.Notes.Should().Be("Policy note – with an en dash");
        policy.MinOfficeMinutesForWfoDay.Should().Be(0);
        policy.FullDayMinutes.Should().Be(480);

        var upload = await db.AttendanceUploads.SingleAsync(u => u.Filename == "October 2026.pdf");
        (await db.AttendanceUploads.SingleAsync(u => u.Status == UploadStatus.Preview)).CommitSummary.Should().BeNull(); // SQL NULL
        upload.Status.Should().Be(UploadStatus.Committed);
        upload.Summary!["columns"]!["employee"]!.GetValue<string>().Should().Be("Code");
        upload.Summary["errors"]![0]!["error"]!.GetValue<string>().Should().Be("unknown employee 'X9' (not in the app yet)");
        upload.CommitSummary!["applied_days"]!.GetValue<int>().Should().Be(1);
        upload.PeriodEnd.Should().Be(new DateOnly(2026, 10, 31));

        var rows = await db.AttendanceUploadRows.OrderBy(r => r.RowNumber).ToListAsync();
        rows[0].WorkMode.Should().Be(WorkMode.Office);
        rows[0].FirstIn.Should().Be(T);
        rows[1].EmployeeId.Should().BeNull();
        rows[1].IsBadge.Should().BeTrue();

        var days = await db.AttendanceDaily.OrderBy(d => d.WorkDate).ToListAsync();
        days[0].Conflicts.Should().Equal("UPLOAD_COLUMN said WFH; office signal from BADGE – checked");
        days[0].WfoSource.Should().Be(WfoSource.UploadColumn);
        days[0].LastOut.Should().Be(T2);
        days[1].OverrideWorkMode.Should().Be(WorkMode.Leave);
        days[1].Conflicts.Should().BeEmpty();
        days[1].FirstIn.Should().BeNull();

        (await db.AuditLogs.SingleAsync(a => a.Action == "LOGIN")).BeforeValues.Should().BeNull(); // JSON text 'null'
        var audit = await db.AuditLogs.SingleAsync(a => a.Action == "USER_UPDATED");
        audit.AfterValues!["note"]!.GetValue<string>().Should().Be("é – ok");
        audit.AfterValues["ratio"]!.GetValue<double>().Should().Be(66.7);
        audit.AfterValues["missing"].Should().BeNull();

        (await db.UserSessions.SingleAsync()).ExpiresAt.Should().Be(T.AddHours(12));
        (await db.LoginAttempts.SingleAsync()).Succeeded.Should().BeTrue();
        (await db.OfficeSites.SingleAsync()).Code.Should().Be("BLR-1");
        (await db.Devices.Include(d => d.User).SingleAsync()).User!.EmployeeCode.Should().Be("Intern - 90001");
        (await db.WifiEvents.SingleAsync()).EventType.Should().Be(WifiEventType.Seen);
        (await db.PresenceEvents.SingleAsync()).Activity.Should().Be("InAMeeting");
        (await db.AttendanceDailyCorrections.SingleAsync()).RequestedStatus.Should().Be(DailyStatus.Present);
        (await db.AttendanceSessions.SingleAsync()).WorkLocation.Should().Be(WorkLocation.Office);
        (await db.AttendanceCorrections.SingleAsync()).RequestType.Should().Be(CorrectionType.ModifySession);
        (await db.LeaveRecords.SingleAsync()).LeaveType.Should().Be(LeaveType.Annual);
    }

    [Fact]
    public async Task Queries_on_timestamps_and_dates_work_on_sqlite_text()
    {
        await using var db = Databases.Context(Databases.FixtureCopy());
        (await db.AttendanceDaily.CountAsync(d => d.FirstIn >= T && d.FirstIn < T2)).Should().Be(1);
        (await db.AttendanceDaily.Where(d => d.WorkDate >= new DateOnly(2026, 10, 6)).Select(d => d.Id).ToListAsync()).Should().HaveCount(1);
        (await db.Users.OrderByDescending(u => u.UpdatedAt).Select(u => u.EmployeeCode).FirstAsync()).Should().Be("E1000");
        (await db.AttendanceUploads.MaxAsync(u => u.PeriodEnd)).Should().Be(new DateOnly(2026, 10, 31));
        (await db.AttendanceUploads.CountAsync(u => u.CommittedAt != null)).Should().Be(1);
    }

    [Fact]
    public async Task Writes_rows_in_the_same_text_formats_as_python()
    {
        var target = await Databases.NewMigratedSqliteAsync();
        var clock = new FakeClock(T);
        await using (var db = Databases.Context(target, clock))
        {
            var user = new User
            {
                EmployeeCode = "E2000", FullName = "Test Person", Email = "t@example.test", PasswordHash = "x",
                Role = Role.Employee, DateJoined = new DateOnly(2026, 6, 1),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            db.WorkSchedules.Add(new WorkSchedule
            {
                Name = "Day", StartTime = new TimeOnly(9, 30), EndTime = new TimeOnly(18, 30), RequiredMinutes = 480,
                WorkingDays = [1, 2, 3, 4, 5], EffectiveFrom = new DateOnly(2024, 1, 1),
            });
            db.AttendanceDaily.Add(new AttendanceDaily
            {
                EmployeeId = user.Id, WorkDate = new DateOnly(2026, 10, 5), FirstIn = T, LastOut = T2,
                Status = DailyStatus.Present, WorkMode = WorkMode.Office, WfoSource = WfoSource.UploadColumn,
                Conflicts = ["a – b"], UploadWorkMode = WorkMode.Office, UploadMinutes = 425,
            });
            db.AuditLogs.Add(new AuditLog
            {
                Action = "USER_CREATED", EntityType = "user", EntityId = "1",
                AfterValues = new JsonObject { ["full_name"] = "Test Person", ["count"] = 3, ["ratio"] = 66.7, ["ok"] = true, ["none"] = null },
            });
            db.AuditLogs.Add(new AuditLog { Action = "LOGIN", EntityType = "user", EntityId = "1", BeforeValues = null, AfterValues = null });
            db.AttendanceUploads.Add(new AttendanceUpload { Filename = "f.csv", FileSha256 = "x", Summary = new JsonObject { ["rows"] = 0 } });
            await db.SaveChangesAsync();
        }

        await using var connection = new SqliteConnection(target.ConnectionString);
        await connection.OpenAsync();
        async Task<object?> Scalar(string sql)
        {
            await using var c = connection.CreateCommand();
            c.CommandText = sql;
            return await c.ExecuteScalarAsync();
        }

        (await Scalar("SELECT created_at FROM users")).Should().Be("2026-10-05 04:52:13.123456");
        (await Scalar("SELECT date_joined FROM users")).Should().Be("2026-06-01");
        (await Scalar("SELECT role FROM users")).Should().Be("EMPLOYEE");
        (await Scalar("SELECT is_active FROM users")).Should().Be(1L);
        (await Scalar("SELECT start_time FROM work_schedules")).Should().Be("09:30:00.000000");
        (await Scalar("SELECT working_days FROM work_schedules")).Should().Be("[1, 2, 3, 4, 5]");
        (await Scalar("SELECT last_out FROM attendance_daily")).Should().Be("2026-10-05 11:30:00.000000");
        (await Scalar("SELECT conflicts FROM attendance_daily")).Should().Be("[\"a \\u2013 b\"]");
        (await Scalar("SELECT wfo_source FROM attendance_daily")).Should().Be("UPLOAD_COLUMN");
        (await Scalar("SELECT before_values IS NULL FROM audit_logs WHERE action = 'LOGIN'")).Should().Be(1L); // P11: Python writes 'null'
        await using (var read = Databases.Context(target))
        {
            (await read.AuditLogs.SingleAsync(a => a.Action == "LOGIN")).BeforeValues.Should().BeNull();
        }
        (await Scalar("SELECT commit_summary IS NULL FROM attendance_uploads")).Should().Be(1L);
        (await Scalar("SELECT status FROM attendance_uploads")).Should().Be("PREVIEW");
        (await Scalar("SELECT after_values FROM audit_logs WHERE action = 'USER_CREATED'")).Should().Be(
            "{\"full_name\": \"Test Person\", \"count\": 3, \"ratio\": 66.7, \"ok\": true, \"none\": null}");
    }

    [Fact]
    public async Task Timestamps_are_set_on_insert_and_updated_on_change()
    {
        var target = await Databases.NewMigratedSqliteAsync();
        var clock = new FakeClock(T);
        await using var db = Databases.Context(target, clock);
        var site = new OfficeSite { Name = "Site", Code = "S1" };
        db.OfficeSites.Add(site);
        db.Holidays.Add(new Holiday { Name = "Day", HolidayDate = new DateOnly(2026, 1, 26) });
        db.AttendancePolicies.Add(new AttendancePolicy());
        await db.SaveChangesAsync();
        site.CreatedAt.Should().Be(T);
        site.UpdatedAt.Should().Be(T);
        (await db.Holidays.SingleAsync()).CreatedAt.Should().Be(T);
        (await db.AttendancePolicies.SingleAsync()).UpdatedAt.Should().Be(T);

        clock.UtcNow = T2;
        site.Name = "Renamed";
        await db.SaveChangesAsync();
        site.CreatedAt.Should().Be(T);
        site.UpdatedAt.Should().Be(T2);
    }

    [PostgresFact]
    public async Task Round_trips_on_postgres()
    {
        var target = await Databases.EmptyPostgresAsync();
        (await new Ats.Infrastructure.Persistence.DatabaseCommands(target, TextWriter.Null).MigrateAsync()).Should().Be(0);
        await using (var db = Databases.Context(target, new FakeClock(T)))
        {
            var user = new User
            {
                EmployeeCode = "E3000", FullName = "Pg Person", Email = "pg@example.test", PasswordHash = "x",
                Role = Role.Admin, DateJoined = new DateOnly(2026, 6, 1),
            };
            db.Users.Add(user);
            await db.SaveChangesAsync();
            db.AttendanceDaily.Add(new AttendanceDaily
            {
                EmployeeId = user.Id, WorkDate = new DateOnly(2026, 10, 5), FirstIn = T.ToOffset(TimeSpan.FromHours(5.5)),
                Status = DailyStatus.Present, Conflicts = ["x"], WorkMode = WorkMode.Wfh,
            });
            db.WorkSchedules.Add(new WorkSchedule
            {
                Name = "Day", StartTime = new TimeOnly(9, 30), EndTime = new TimeOnly(18, 30), RequiredMinutes = 480,
                WorkingDays = [1, 2], EffectiveFrom = new DateOnly(2024, 1, 1),
            });
            await db.SaveChangesAsync();
        }

        await using var read = Databases.Context(target);
        var day = await read.AttendanceDaily.SingleAsync();
        day.FirstIn.Should().Be(T);
        day.FirstIn!.Value.Offset.Should().Be(TimeSpan.Zero);
        day.Conflicts.Should().Equal("x");
        day.WorkMode.Should().Be(WorkMode.Wfh);
        (await read.WorkSchedules.SingleAsync()).WorkingDays.Should().Equal(1, 2);
        (await read.Users.SingleAsync()).Role.Should().Be(Role.Admin);
    }
}
