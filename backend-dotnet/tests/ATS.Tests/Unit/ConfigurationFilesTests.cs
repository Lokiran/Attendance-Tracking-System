using Ats.Application.Configuration;
using Ats.Infrastructure.Database;
using FluentAssertions;
using Npgsql;

namespace Ats.Tests.Unit;

public sealed class EnvFileTests
{
    [Fact]
    public void Reads_python_dotenv_syntax()
    {
        const string text = """
            # comment
            APP_ENV=development
            export SECRET_KEY = abc123   # inline comment
            DATABASE_URL="postgresql+psycopg://ats:p%40ss@localhost:5433/ats_db"
            SINGLE='literal ${APP_ENV} # not a comment'
            DOUBLE="line\nbreak \"quoted\""
            EXPANDED=${APP_ENV}-x
            DEFAULTED=${MISSING_VAR:-fallback}
            FROM_ENV=${ATS_TEST_FROM_ENV}
            EMPTY=
            not a pair
            """;
        var env = new Dictionary<string, string> { ["ATS_TEST_FROM_ENV"] = "outside" };
        var values = EnvFile.Parse(text, k => env.TryGetValue(k, out var v) ? v : null);

        values["APP_ENV"].Should().Be("development");
        values["SECRET_KEY"].Should().Be("abc123");
        values["DATABASE_URL"].Should().Be("postgresql+psycopg://ats:p%40ss@localhost:5433/ats_db");
        values["SINGLE"].Should().Be("literal ${APP_ENV} # not a comment");
        values["DOUBLE"].Should().Be("line\nbreak \"quoted\"");
        values["EXPANDED"].Should().Be("development-x");
        values["DEFAULTED"].Should().Be("fallback");
        values["FROM_ENV"].Should().Be("outside");
        values["EMPTY"].Should().BeEmpty();
        values.Should().NotContainKey("not a pair");
        values["app_env"].Should().Be("development"); // keys are case-insensitive, like pydantic-settings
    }

    [Fact]
    public void A_requested_env_file_must_exist()
    {
        var env = new Dictionary<string, string?> { ["ATS_ENV_FILE"] = "does-not-exist.env", ["ATS_HOME"] = Path.GetTempPath() };
        var act = () => EnvironmentInfo.Select(k => env.TryGetValue(k, out var v) ? v : null);
        act.Should().Throw<SettingsException>().WithMessage("ATS_ENV_FILE points to*which does not exist");
    }

    [Fact]
    public void Relative_paths_resolve_against_the_env_files_directory()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"ats-env-{Guid.NewGuid():N}")).FullName;
        var file = Path.Combine(dir, ".env.sqlite");
        File.WriteAllText(file, "DATABASE_URL=sqlite:///data/ats-dev.sqlite3\n");
        var env = new Dictionary<string, string?> { ["ATS_ENV_FILE"] = file };
        var info = EnvironmentInfo.Select(k => env.TryGetValue(k, out var v) ? v : null);

        info.EnvFile.Should().Be(file);
        info.BaseDirectory.Should().Be(dir);
        var target = DatabaseTarget.Parse(info.Values["DATABASE_URL"], info.BaseDirectory);
        target.SqliteFile.Should().Be(Path.Combine(dir, "data", "ats-dev.sqlite3"));
    }
}

public sealed class DatabaseTargetTests
{
    [Theory]
    [InlineData("postgresql+psycopg://ats:secret@db.local:5433/ats_db")]
    [InlineData("postgresql://ats:secret@db.local:5433/ats_db")]
    [InlineData("postgres://ats:secret@db.local:5433/ats_db")]
    public void Postgres_urls_become_npgsql_connection_strings(string url)
    {
        var t = DatabaseTarget.Parse(url, "/base");
        t.Provider.Should().Be(DatabaseProvider.PostgreSql);
        t.DialectName.Should().Be("postgresql");
        var b = new NpgsqlConnectionStringBuilder(t.ConnectionString);
        (b.Host, b.Port, b.Username, b.Password, b.Database, b.Timeout).Should().Be(("db.local", 5433, "ats", "secret", "ats_db", 10));
        t.Description.Should().Be("postgresql ats@db.local:5433/ats_db").And.NotContain("secret");
    }

    [Fact]
    public void Percent_encoded_credentials_and_options()
    {
        var t = DatabaseTarget.Parse("postgresql+psycopg://ats:p%40ss%2Fw%3Ard@localhost/ats?sslmode=require&connect_timeout=5", "/b");
        var b = new NpgsqlConnectionStringBuilder(t.ConnectionString);
        b.Password.Should().Be("p@ss/w:rd");
        b.Port.Should().Be(5432);
        b.SslMode.Should().Be(SslMode.Require);
        b.Timeout.Should().Be(5);
    }

    [Fact]
    public void Sqlite_relative_absolute_and_memory()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), "backend");
        DatabaseTarget.Parse("sqlite:///data/ats-dev.sqlite3", baseDir).SqliteFile
            .Should().Be(Path.Combine(baseDir, "data", "ats-dev.sqlite3"));

        var absolute = Path.Combine(Path.GetTempPath(), "x.sqlite3");
        var t = DatabaseTarget.Parse("sqlite:///" + absolute.Replace('\\', '/'), baseDir);
        t.SqliteFile.Should().Be(Path.GetFullPath(absolute));
        t.ConnectionString.Should().Contain("Mode=ReadWrite"); // never creates a database file
        t.Description.Should().StartWith("SQLite file ");
        t.DialectName.Should().Be("sqlite");

        DatabaseTarget.Parse("sqlite://", baseDir).Description.Should().Be("SQLite in-memory database");
        DatabaseTarget.Parse("sqlite:///:memory:", baseDir).SqliteFile.Should().BeNull();
    }

    [Theory]
    [InlineData("mysql://u:p@localhost/db")]
    [InlineData("localhost:5432")]
    [InlineData("")]
    public void Other_databases_are_refused(string url) =>
        ((Action)(() => DatabaseTarget.Parse(url, "/b"))).Should().Throw<SettingsException>();

    [Fact]
    public void A_missing_sqlite_file_refuses_startup()
    {
        var target = DatabaseTarget.Parse("sqlite:///" + Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.db").Replace('\\', '/'), "/b");
        var settings = new AtsSettings();
        ((Action)(() => DatabaseStartup.EnsureReady(target, settings))).Should().Throw<DatabaseNotReadyException>()
            .WithMessage("SQLite database file not found*");
    }
}
