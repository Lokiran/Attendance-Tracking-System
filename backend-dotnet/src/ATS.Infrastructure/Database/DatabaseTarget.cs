using System.Globalization;
using System.Text.RegularExpressions;
using Ats.Application.Configuration;
using Microsoft.Data.Sqlite;
using Npgsql;

namespace Ats.Infrastructure.Database;

public enum DatabaseProvider
{
    PostgreSql,
    Sqlite,
}

/// <summary>
/// The database named by <c>DATABASE_URL</c>, in the SQLAlchemy URL form the Python backend uses
/// (<c>postgresql+psycopg://user:pass@host:5432/db</c>, <c>postgres://…</c>, <c>sqlite:///data/ats-dev.sqlite3</c>).
/// Selection is explicit: any other scheme is refused, and nothing ever falls back to another database.
/// </summary>
public sealed partial record DatabaseTarget(DatabaseProvider Provider, string ConnectionString, string Description,
                                            string? SqliteFile)
{
    /// <summary>The SQLAlchemy dialect name (<c>/api/health/db</c> reports it).</summary>
    public string DialectName => Provider == DatabaseProvider.PostgreSql ? "postgresql" : "sqlite";

    public static DatabaseTarget Parse(string url, string baseDirectory)
    {
        ArgumentNullException.ThrowIfNull(url);
        url = url.Trim();
        var pg = PostgresUrl().Match(url);
        if (pg.Success)
        {
            return Postgres(pg);
        }

        var lite = SqliteUrl().Match(url);
        if (lite.Success)
        {
            return Sqlite(lite.Groups["path"].Value, baseDirectory);
        }

        throw new SettingsException("DATABASE_URL must be a PostgreSQL (postgresql://...) or SQLite (sqlite:///...) URL");
    }

    private static DatabaseTarget Postgres(Match m)
    {
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = Unescape(m.Groups["host"].Value) is { Length: > 0 } host ? host : "localhost",
            Port = m.Groups["port"].Success ? int.Parse(m.Groups["port"].Value, CultureInfo.InvariantCulture) : 5432,
            Database = Unescape(m.Groups["db"].Value),
            // Fail fast (-> HTTP 503) instead of hanging when the server is unreachable (Python: connect_timeout=10).
            Timeout = 10,
        };
        if (m.Groups["user"].Success)
        {
            b.Username = Unescape(m.Groups["user"].Value);
        }

        if (m.Groups["password"].Success)
        {
            b.Password = Unescape(m.Groups["password"].Value);
        }

        foreach (var (key, value) in Query(m.Groups["query"].Value))
        {
            switch (key)
            {
                case "sslmode":
                    b.SslMode = Enum.Parse<SslMode>(value.Replace("-", "", StringComparison.Ordinal), ignoreCase: true);
                    break;
                case "connect_timeout":
                    b.Timeout = int.Parse(value, CultureInfo.InvariantCulture);
                    break;
                case "application_name":
                    b.ApplicationName = value;
                    break;
                case "host":
                    b.Host = value;
                    break;
                default:
                    throw new SettingsException($"DATABASE_URL option '{key}' is not supported by the .NET backend");
            }
        }

        var port = m.Groups["port"].Success ? m.Groups["port"].Value : "";
        var description = $"postgresql {b.Username}@{b.Host}:{port}/{b.Database}";
        return new DatabaseTarget(DatabaseProvider.PostgreSql, b.ConnectionString, description, null);
    }

    private static DatabaseTarget Sqlite(string rawPath, string baseDirectory)
    {
        var path = Uri.UnescapeDataString(rawPath);
        if (path is "" or ":memory:")
        {
            var memory = new SqliteConnectionStringBuilder { DataSource = ":memory:" }.ConnectionString;
            return new DatabaseTarget(DatabaseProvider.Sqlite, memory, "SQLite in-memory database", null);
        }

        // sqlite:////abs/path and sqlite:///C:/path are absolute; anything else is relative to the backend directory.
        var full = Path.IsPathRooted(path) ? Path.GetFullPath(path) : Path.GetFullPath(Path.Combine(baseDirectory, path));
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = full,
            // Never create a database implicitly: a missing file is an error, not a new empty database.
            Mode = SqliteOpenMode.ReadWrite,
            ForeignKeys = true,
            DefaultTimeout = 15,
        };
        return new DatabaseTarget(DatabaseProvider.Sqlite, builder.ConnectionString, $"SQLite file {full}", full);
    }

    private static string Unescape(string value) => Uri.UnescapeDataString(value);

    private static IEnumerable<(string Key, string Value)> Query(string query)
    {
        foreach (var part in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var key = Unescape(eq < 0 ? part : part[..eq]).ToLowerInvariant();
            yield return (key, eq < 0 ? "" : Unescape(part[(eq + 1)..]));
        }
    }

    [GeneratedRegex(@"^postgres(?:ql)?(?:\+\w+)?://(?:(?<user>[^:@/]*)(?::(?<password>[^@/]*))?@)?(?<host>[^:/?]*)(?::(?<port>\d+))?(?:/(?<db>[^?]*))?(?:\?(?<query>.*))?$")]
    private static partial Regex PostgresUrl();

    [GeneratedRegex(@"^sqlite(?:\+\w+)?://(?:/(?<path>[^?]*))?(?:\?.*)?$")]
    private static partial Regex SqliteUrl();
}
