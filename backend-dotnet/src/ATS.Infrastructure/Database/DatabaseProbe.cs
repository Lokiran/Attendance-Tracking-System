using System.Data.Common;
using Ats.Application.Configuration;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Ats.Infrastructure.Database;

/// <summary>The application database cannot be used yet (missing or uninitialised SQLite file).</summary>
public sealed class DatabaseNotReadyException(string message) : Exception(message)
{
    public DatabaseNotReadyException()
        : this("The database is not ready")
    {
    }

    public DatabaseNotReadyException(string message, Exception innerException)
        : this(message + ": " + innerException.Message)
    {
    }
}

/// <summary>Connectivity check for <c>/api/health/db</c> (<c>SELECT 1</c>).</summary>
public interface IDatabaseProbe
{
    string DialectName { get; }

    Task<bool> CanConnectAsync(CancellationToken cancellationToken);
}

public sealed partial class DatabaseProbe(DatabaseTarget target, ILogger<DatabaseProbe> logger) : IDatabaseProbe
{
    public string DialectName => target.DialectName;

    public async Task<bool> CanConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = Open(target);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT 1";
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbException ex)
        {
            LogUnreachable(logger, ex.GetType().Name);
            return false;
        }
    }

    public static DbConnection Open(DatabaseTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        return target.Provider == DatabaseProvider.PostgreSql
            ? new NpgsqlConnection(target.ConnectionString)
            : new SqliteConnection(target.ConnectionString);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "database error: {ErrorType}")]
    private static partial void LogUnreachable(ILogger logger, string errorType);
}

/// <summary>
/// Startup checks (<c>ensure_sqlite_ready</c> in Python): a SQLite URL must name an existing file, since
/// connecting to a missing file would silently create an empty database. The app never creates or migrates a
/// database implicitly. PostgreSQL is not checked here: if it is unreachable, requests answer 503 (no fallback).
/// An existing SQLite file without the application tables is refused as well.
/// </summary>
public static class DatabaseStartup
{
    public static void EnsureReady(DatabaseTarget target, AtsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(settings);
        if (target.SqliteFile is { } file && !File.Exists(file))
        {
            throw new DatabaseNotReadyException(
                $"SQLite database file not found: {file}. Initialise it explicitly first (see backend-dotnet/README.md); " +
                "the app never creates a database on its own.");
        }

        if (target.SqliteFile is not null)
        {
            using var connection = new SqliteConnection(target.ConnectionString);
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            var present = new HashSet<string>(StringComparer.Ordinal);
            using (var reader = command.ExecuteReader())
            {
                while (reader.Read())
                {
                    present.Add(reader.GetString(0));
                }
            }

            var missing = RequiredTables.Where(t => !present.Contains(t)).ToList();
            if (missing.Count > 0)
            {
                throw new DatabaseNotReadyException(
                    $"SQLite database {target.SqliteFile} has no application schema (missing {string.Join(", ", missing.Order(StringComparer.Ordinal))}). " +
                    "Initialise it explicitly: dotnet run --project src/ATS.Api -- migrate");
            }
        }
    }

    /// <summary>Tables the application needs (all of Alembic head 0006).</summary>
    public static readonly IReadOnlyList<string> RequiredTables =
    [
        "departments", "users", "work_schedules", "holidays", "attendance_policy", "attendance_daily",
        "attendance_uploads", "attendance_upload_rows", "audit_logs", "user_sessions", "login_attempts",
        "office_sites", "devices", "wifi_events", "presence_events", "attendance_daily_corrections",
        "attendance_sessions", "attendance_corrections", "leave_records",
    ];
}
