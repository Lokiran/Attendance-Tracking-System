using Ats.Application.Configuration;
using Ats.Infrastructure.Database;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Ats.Tests.Persistence;

/// <summary>Explicit database selection at startup (port of backend/app/tests/test_database_selection.py, the startup checks).</summary>
public sealed class DatabaseStartupTests
{
    private static readonly AtsSettings Settings = new();

    private static DatabaseTarget Sqlite(string path) => DatabaseTarget.Parse("sqlite:///" + path.Replace('\\', '/'), Path.GetTempPath());

    [Fact]
    public void A_missing_sqlite_file_is_refused_and_not_created()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ats-nope-{Guid.NewGuid():N}.sqlite3");
        FluentActions.Invoking(() => DatabaseStartup.EnsureReady(Sqlite(path), Settings)).Should().Throw<DatabaseNotReadyException>().WithMessage("*not found*");
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void An_uninitialised_sqlite_file_is_refused()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ats-empty-{Guid.NewGuid():N}.sqlite3");
        using (var connection = new SqliteConnection($"Data Source={path}"))
        {
            connection.Open();
        }

        SqliteConnection.ClearAllPools();
        FluentActions.Invoking(() => DatabaseStartup.EnsureReady(Sqlite(path), Settings)).Should().Throw<DatabaseNotReadyException>().WithMessage("*no application schema*");
    }

    [Fact]
    public void An_initialised_sqlite_file_is_accepted() =>
        FluentActions.Invoking(() => DatabaseStartup.EnsureReady(Sqlite(TestEnvironment.NewSqliteFile()), Settings)).Should().NotThrow();

    /// <summary>No connection is attempted for PostgreSQL: an unreachable server yields 503s, never a fallback.</summary>
    [Fact]
    public void Postgres_is_not_probed_at_startup()
    {
        var target = DatabaseTarget.Parse("postgresql+psycopg://u:p@203.0.113.1:5432/x_test", Path.GetTempPath());
        var watch = System.Diagnostics.Stopwatch.StartNew();
        FluentActions.Invoking(() => DatabaseStartup.EnsureReady(target, Settings)).Should().NotThrow();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void The_env_template_names_postgres_explicitly()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "ATS.sln")))
        {
            root = root.Parent;
        }

        var template = File.ReadAllText(Path.Combine(root!.FullName, ".env.example"));
        template.Should().Contain("DATABASE_URL=postgresql");
    }
}
