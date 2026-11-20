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
/// The table check is added with the schema (phase 2).
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
    }
}
