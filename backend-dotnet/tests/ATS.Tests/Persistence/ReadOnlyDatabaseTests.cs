using Ats.Infrastructure.Database;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Ats.Tests.Persistence;

/// <summary>A fact that runs only when ATS_TEST_READONLY_DB_URL names a database to read.</summary>
public sealed class ReadOnlyDatabaseFactAttribute : FactAttribute
{
    public ReadOnlyDatabaseFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("ATS_TEST_READONLY_DB_URL") is null)
        {
            Skip = "Set ATS_TEST_READONLY_DB_URL to read an existing database (nothing is written)";
        }
    }
}

/// <summary>
/// Opt-in check of an existing database (for example a copy of production): every row of every table is loaded
/// through the EF model, so unusual stored values (JSON, enum text, timestamps) surface before cutover.
/// Read-only: no SaveChanges, and the context does not track entities.
/// </summary>
public sealed class ReadOnlyDatabaseTests(ITestOutputHelper output)
{
    [ReadOnlyDatabaseFact]
    public async Task Every_row_of_every_table_loads_through_the_model()
    {
        var target = DatabaseTarget.Parse(Environment.GetEnvironmentVariable("ATS_TEST_READONLY_DB_URL")!, Directory.GetCurrentDirectory());
        await using var db = Databases.Context(target);
        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
        db.ChangeTracker.AutoDetectChangesEnabled = false;

        var counts = new Dictionary<string, int>
        {
            ["departments"] = (await db.Departments.ToListAsync()).Count,
            ["users"] = (await db.Users.ToListAsync()).Count,
            ["work_schedules"] = (await db.WorkSchedules.ToListAsync()).Count,
            ["holidays"] = (await db.Holidays.ToListAsync()).Count,
            ["attendance_policy"] = (await db.AttendancePolicies.ToListAsync()).Count,
            ["attendance_daily"] = (await db.AttendanceDaily.ToListAsync()).Count,
            ["attendance_uploads"] = (await db.AttendanceUploads.ToListAsync()).Count,
            ["attendance_upload_rows"] = (await db.AttendanceUploadRows.ToListAsync()).Count,
            ["audit_logs"] = (await db.AuditLogs.ToListAsync()).Count,
            ["user_sessions"] = (await db.UserSessions.ToListAsync()).Count,
            ["login_attempts"] = (await db.LoginAttempts.ToListAsync()).Count,
            ["office_sites"] = (await db.OfficeSites.ToListAsync()).Count,
            ["devices"] = (await db.Devices.ToListAsync()).Count,
            ["wifi_events"] = (await db.WifiEvents.ToListAsync()).Count,
            ["presence_events"] = (await db.PresenceEvents.ToListAsync()).Count,
            ["attendance_daily_corrections"] = (await db.AttendanceDailyCorrections.ToListAsync()).Count,
            ["attendance_sessions"] = (await db.AttendanceSessions.ToListAsync()).Count,
            ["attendance_corrections"] = (await db.AttendanceCorrections.ToListAsync()).Count,
            ["leave_records"] = (await db.LeaveRecords.ToListAsync()).Count,
        };
        foreach (var (table, count) in counts)
        {
            output.WriteLine($"{table}: {count}");
        }

        counts.Values.Sum().Should().BeGreaterThan(0);
    }
}
