using System.Reflection;

namespace Ats.Infrastructure.Persistence.Migrations;

/// <summary>SQL scripts embedded next to the migrations (the exact Alembic DDL).</summary>
internal static class MigrationSql
{
    public static string Load(string fileName)
    {
        var assembly = typeof(MigrationSql).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + fileName, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    /// <summary>Every application table, dependents first (what `alembic downgrade base` removes).</summary>
    public static readonly string[] Tables =
    [
        "attendance_upload_rows", "attendance_daily", "attendance_daily_corrections", "attendance_corrections",
        "attendance_sessions", "leave_records", "wifi_events", "presence_events", "devices", "office_sites",
        "audit_logs", "user_sessions", "login_attempts", "holidays", "work_schedules", "attendance_policy",
        "attendance_uploads", "users", "departments", "alembic_version",
    ];
}
