using Microsoft.Data.Sqlite;
using Npgsql;

namespace Ats.Infrastructure.Database;

public enum DatabaseErrorKind
{
    /// <summary>Not a database error the API translates (500 if unhandled).</summary>
    None,

    /// <summary>The database cannot be reached or is busy: 503 <c>database_unavailable</c> (Python: OperationalError).</summary>
    Unavailable,

    /// <summary>A constraint violation no service translated: 409 <c>conflict</c> (Python: IntegrityError).</summary>
    Conflict,
}

public static class DatabaseErrors
{
    public static DatabaseErrorKind Classify(Exception? exception)
    {
        for (var ex = exception; ex is not null; ex = ex.InnerException)
        {
            switch (ex)
            {
                case PostgresException pg:
                    if (pg.SqlState.StartsWith("23", StringComparison.Ordinal))
                    {
                        return DatabaseErrorKind.Conflict;
                    }

                    // connection (08), resources (53), operator intervention (57), deadlock / serialization (40)
                    return pg.SqlState[..2] is "08" or "53" or "57" or "40"
                        ? DatabaseErrorKind.Unavailable
                        : DatabaseErrorKind.None;
                case NpgsqlException:
                    return DatabaseErrorKind.Unavailable; // network, timeout, pool exhausted
                case SqliteException lite:
                    return lite.SqliteErrorCode switch
                    {
                        19 => DatabaseErrorKind.Conflict,                       // SQLITE_CONSTRAINT
                        5 or 6 or 10 or 14 => DatabaseErrorKind.Unavailable,    // BUSY, LOCKED, IOERR, CANTOPEN
                        _ => DatabaseErrorKind.None,
                    };
                default:
                    break;
            }
        }

        return DatabaseErrorKind.None;
    }
}
