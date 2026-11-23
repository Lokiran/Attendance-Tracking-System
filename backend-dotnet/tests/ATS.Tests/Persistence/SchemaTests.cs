using Ats.Infrastructure.Database;
using Ats.Infrastructure.Persistence;
using Ats.Infrastructure.Persistence.Schema;
using FluentAssertions;
using Microsoft.Data.Sqlite;

namespace Ats.Tests.Persistence;

/// <summary>
/// The EF baseline creates exactly the schema Alembic creates (head 0006), and existing Alembic databases are
/// adopted without any change other than EF's history table (test_migrations.py's drift check, for .NET).
/// </summary>
[Collection(Databases.PostgresCollection)]
public sealed class SchemaTests
{
    private static async Task<SchemaCatalog> CatalogOf(DatabaseTarget target)
    {
        await using var connection = DatabaseProbe.Open(target);
        return await SchemaCatalog.ReadAsync(connection, target.Provider);
    }

    private static async Task<List<string>> TablesOf(DatabaseTarget target) =>
        [.. (await CatalogOf(target)).Tables];

    [Fact]
    public void Expected_catalogs_cover_the_whole_schema()
    {
        SchemaCatalog.Expected(DatabaseProvider.PostgreSql).Tables.Should().HaveCount(20).And.Contain("alembic_version");
        SchemaCatalog.Expected(DatabaseProvider.Sqlite).Tables.Should().HaveCount(20);
        var pg = SchemaCatalog.Expected(DatabaseProvider.PostgreSql).Entries;
        pg.Should().Contain("extension btree_gist");
        pg.Should().Contain(e => e.StartsWith("constraint attendance_sessions.ex_attendance_no_overlap EXCLUDE USING gist", StringComparison.Ordinal));
        pg.Should().Contain(e => e.StartsWith("index departments.uq_departments_name_lower CREATE UNIQUE INDEX", StringComparison.Ordinal)
                                 && e.Contains("lower((name)::text)", StringComparison.Ordinal));
        pg.Should().Contain("column users.id integer NOT NULL DEFAULT nextval('users_id_seq'::regclass)");
    }

    [Fact]
    public async Task Migrate_creates_exactly_the_alembic_schema_on_sqlite()
    {
        var target = await Databases.NewMigratedSqliteAsync();
        var (missing, unexpected) = (await CatalogOf(target)).CompareTo(SchemaCatalog.Expected(DatabaseProvider.Sqlite));
        missing.Should().BeEmpty();
        unexpected.Should().BeEmpty();

        await using var connection = new SqliteConnection(target.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT version_num FROM alembic_version";
        (await command.ExecuteScalarAsync()).Should().Be("0006"); // the Python backend accepts it too
    }

    [Fact]
    public async Task The_python_fixture_matches_the_expected_schema()
    {
        var (missing, unexpected) = (await CatalogOf(Databases.FixtureCopy())).CompareTo(SchemaCatalog.Expected(DatabaseProvider.Sqlite));
        missing.Should().BeEmpty();
        unexpected.Should().BeEmpty();
    }

    [Fact]
    public async Task Migrate_is_idempotent_and_refuses_alembic_or_unknown_databases()
    {
        var target = await Databases.NewMigratedSqliteAsync();
        var output = new StringWriter();
        (await new DatabaseCommands(target, output).MigrateAsync()).Should().Be(0);
        output.ToString().Should().Contain("up to date");

        output = new StringWriter();
        (await new DatabaseCommands(Databases.FixtureCopy(), output).MigrateAsync()).Should().Be(2);
        output.ToString().Should().Contain("created by Alembic").And.Contain("ef-baseline");

        var other = Databases.Sqlite(Path.Combine(Path.GetTempPath(), $"ats-other-{Guid.NewGuid():N}.sqlite3"));
        await using (var c = new SqliteConnection(other.ConnectionString.Replace("Mode=ReadWrite", "Mode=ReadWriteCreate", StringComparison.Ordinal)))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE TABLE something_else (id INTEGER)";
            await cmd.ExecuteNonQueryAsync();
        }

        output = new StringWriter();
        (await new DatabaseCommands(other, output).MigrateAsync()).Should().Be(2);
        output.ToString().Should().Contain("not empty");
    }

    [Fact]
    public async Task Baseline_adopts_an_alembic_database_by_adding_only_the_history_table()
    {
        var target = Databases.FixtureCopy();
        var before = await CatalogOf(target);
        var output = new StringWriter();

        (await new DatabaseCommands(target, output).BaselineAsync()).Should().Be(0);
        output.ToString().Should().Contain("schema matches Alembic head 0006").And.Contain("recorded");

        (await CatalogOf(target)).Entries.Should().Equal(before.Entries); // nothing of the application schema changed
        (await TablesOf(target)).Should().Contain("alembic_version");
        await using (var context = Databases.Context(target))
        {
            (await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.GetAppliedMigrationsAsync(context.Database))
                .Should().ContainSingle().Which.Should().EndWith("_Baseline");
        }

        output = new StringWriter();
        (await new DatabaseCommands(target, output).BaselineAsync()).Should().Be(0);
        output.ToString().Should().Contain("already adopted");
        (await new DatabaseCommands(target, TextWriter.Null).MigrateAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Baseline_changes_nothing_when_the_schema_differs()
    {
        var target = Databases.FixtureCopy();
        await using (var c = new SqliteConnection(target.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "CREATE INDEX ix_extra ON users (full_name)";
            await cmd.ExecuteNonQueryAsync();
        }

        var output = new StringWriter();
        (await new DatabaseCommands(target, output).BaselineAsync()).Should().Be(2);
        output.ToString().Should().Contain("unexpected: index ix_extra").And.Contain("nothing was changed");
        (await TablesOf(target)).Should().NotContain(SchemaCatalog.HistoryTable);
    }

    [Fact]
    public async Task Baseline_refuses_a_database_that_is_not_alembic_head()
    {
        var target = Databases.FixtureCopy();
        await using (var c = new SqliteConnection(target.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE alembic_version SET version_num = '0005'";
            await cmd.ExecuteNonQueryAsync();
        }

        var output = new StringWriter();
        (await new DatabaseCommands(target, output).BaselineAsync()).Should().Be(2);
        output.ToString().Should().Contain("Alembic version is 0005");
    }

    [Fact]
    public void Sqlite_create_table_statements_compare_regardless_of_constraint_order()
    {
        const string a = "CREATE TABLE t (\n\tid INTEGER NOT NULL, \n\tname VARCHAR(5) DEFAULT 'a,b', \n\tCONSTRAINT pk_t PRIMARY KEY (id), \n\tCONSTRAINT ck_t CHECK (name IN ('x', 'y'))\n)";
        const string b = "CREATE TABLE t (id INTEGER NOT NULL, name VARCHAR(5) DEFAULT 'a,b', CONSTRAINT ck_t CHECK (name IN ('x', 'y')), CONSTRAINT pk_t PRIMARY KEY (id))";
        SchemaCatalog.Normalize(a).Should().Be(SchemaCatalog.Normalize(b));
        SchemaCatalog.Normalize("CREATE TABLE t (b INTEGER, a INTEGER)").Should()
            .NotBe(SchemaCatalog.Normalize("CREATE TABLE t (a INTEGER, b INTEGER)")); // column order still counts
    }

    // ------------------------------------------------------------------ PostgreSQL

    [PostgresFact]
    public async Task Migrate_creates_exactly_the_alembic_schema_on_postgres_and_baseline_adopts_it()
    {
        var target = await Databases.EmptyPostgresAsync();
        (await new DatabaseCommands(target, TextWriter.Null).MigrateAsync()).Should().Be(0);
        var (missing, unexpected) = (await CatalogOf(target)).CompareTo(SchemaCatalog.Expected(DatabaseProvider.PostgreSql));
        missing.Should().BeEmpty();
        unexpected.Should().BeEmpty();

        // An Alembic-built database has the same schema without EF's history: adopt it.
        await using (var c = new Npgsql.NpgsqlConnection(target.ConnectionString))
        {
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = $"DROP TABLE \"{SchemaCatalog.HistoryTable}\"";
            await cmd.ExecuteNonQueryAsync();
        }

        var output = new StringWriter();
        (await new DatabaseCommands(target, output).BaselineAsync()).Should().Be(0, output.ToString());
        (await new DatabaseCommands(target, TextWriter.Null).MigrateAsync()).Should().Be(0);
    }
}
