using Ats.Application.Common;
using Ats.Infrastructure.Database;
using Ats.Infrastructure.Persistence;
using Npgsql;

namespace Ats.Tests.Persistence;

public sealed class FakeClock(DateTimeOffset now) : IClock
{
    public DateTimeOffset UtcNow { get; set; } = now;
}

internal static class Databases
{
    public static string FixturePath => Path.Combine(AppContext.BaseDirectory, "Fixtures", "python-0006.sqlite3");

    /// <summary>A private copy of the Python-written fixture (Alembic head 0006, a few rows per table).</summary>
    public static DatabaseTarget FixtureCopy()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ats-fixture-{Guid.NewGuid():N}.sqlite3");
        File.Copy(FixturePath, path);
        return Sqlite(path);
    }

    /// <summary>A new SQLite database created by the EF migrations.</summary>
    public static async Task<DatabaseTarget> NewMigratedSqliteAsync()
    {
        var target = Sqlite(Path.Combine(Path.GetTempPath(), $"ats-ef-{Guid.NewGuid():N}.sqlite3"));
        var code = await new DatabaseCommands(target, TextWriter.Null).MigrateAsync();
        if (code != 0)
        {
            throw new InvalidOperationException("migrate failed");
        }

        return target;
    }

    public static DatabaseTarget Sqlite(string path) =>
        DatabaseTarget.Parse("sqlite:///" + path.Replace('\\', '/'), Path.GetTempPath());

    public static AtsDbContext Context(DatabaseTarget target, IClock? clock = null)
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<AtsDbContext>();
        AtsDbContextOptions.Configure(options, target);
        return new AtsDbContext(options.Options, clock);
    }

    // ------------------------------------------------------------------ PostgreSQL (opt-in)

    /// <summary>
    /// ATS_TEST_POSTGRES_URL: a disposable PostgreSQL database (its name must contain "test"); every table in it is
    /// dropped. Like the Python suite's TEST_DATABASE_URL. Unset: the PostgreSQL tests are skipped, unless
    /// ATS_REQUIRE_POSTGRES=1 (CI), which makes them fail instead.
    /// </summary>
    public static string? PostgresUrl => Environment.GetEnvironmentVariable("ATS_TEST_POSTGRES_URL");

    public static async Task<DatabaseTarget> EmptyPostgresAsync()
    {
        var target = DatabaseTarget.Parse(PostgresUrl ?? throw new InvalidOperationException(
            "ATS_TEST_POSTGRES_URL is not set (PostgreSQL tests are required here)"), Path.GetTempPath());
        var database = new NpgsqlConnectionStringBuilder(target.ConnectionString).Database ?? "";
        if (!database.Contains("test", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Refusing to wipe '{database}': the test database name must contain 'test'");
        }

        await using var connection = new NpgsqlConnection(target.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            DO $$ DECLARE r record; BEGIN
              FOR r IN SELECT tablename FROM pg_tables WHERE schemaname = 'public' LOOP
                EXECUTE 'DROP TABLE IF EXISTS public.' || quote_ident(r.tablename) || ' CASCADE';
              END LOOP;
              FOR r IN SELECT sequence_name FROM information_schema.sequences WHERE sequence_schema = 'public' LOOP
                EXECUTE 'DROP SEQUENCE IF EXISTS public.' || quote_ident(r.sequence_name) || ' CASCADE';
              END LOOP;
            END $$;
            """;
        await command.ExecuteNonQueryAsync();
        return target;
    }
}

/// <summary>A fact that needs a PostgreSQL test database (see <see cref="Databases.PostgresUrl"/>).</summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (Databases.PostgresUrl is null && Environment.GetEnvironmentVariable("ATS_REQUIRE_POSTGRES") != "1")
        {
            Skip = "Set ATS_TEST_POSTGRES_URL to a disposable PostgreSQL database to run this test";
        }
    }
}
