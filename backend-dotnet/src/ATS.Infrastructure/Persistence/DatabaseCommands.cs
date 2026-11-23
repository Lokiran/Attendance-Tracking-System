using Ats.Infrastructure.Database;
using Ats.Infrastructure.Persistence.Schema;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Ats.Infrastructure.Persistence;

/// <summary>
/// Explicit database maintenance (the API itself never creates or migrates a database):
/// <list type="bullet">
/// <item><c>migrate</c>: create the schema in a new, empty database (EF migrations; Alembic's exact DDL).</item>
/// <item><c>ef-baseline</c>: adopt an existing Alembic database (head 0006). Nothing is changed unless its schema
/// matches the expected one exactly; then only EF's history table is added and the baseline recorded.
/// alembic_version stays, so the Python backend keeps working.</item>
/// <item><c>schema-check</c>: compare the database with the expected schema (read-only).</item>
/// <item><c>schema-catalog</c>: write the database's schema catalog to a file (read-only).</item>
/// </list>
/// </summary>
public sealed class DatabaseCommands(DatabaseTarget target, TextWriter output)
{
    public const string AlembicHead = "0006";

    public static readonly string[] Names = ["migrate", "ef-baseline", "schema-check", "schema-catalog"];

    public async Task<int> RunAsync(string command, IReadOnlyList<string> args, CancellationToken ct = default) =>
        command switch
        {
            "migrate" => await MigrateAsync(ct).ConfigureAwait(false),
            "ef-baseline" => await BaselineAsync(ct).ConfigureAwait(false),
            "schema-check" => await CheckAsync(ct).ConfigureAwait(false),
            "schema-catalog" => await CatalogAsync(args.Count > 0 ? args[0] : null, ct).ConfigureAwait(false),
            _ => throw new ArgumentException($"Unknown command '{command}'", nameof(command)),
        };

    public async Task<int> MigrateAsync(CancellationToken ct = default)
    {
        if (target.SqliteFile is { } file && Path.GetDirectoryName(Path.GetFullPath(file)) is { } folder && !Directory.Exists(folder))
        {
            // Only the database file is created, never folders: a typo in the path must not create a new tree.
            await output.WriteLineAsync($"The folder for the SQLite file does not exist: {folder}. Create it first.").ConfigureAwait(false);
            return 2;
        }

        await using var context = AtsDbContextOptions.CreateMigrationContext(target, allowCreateSqliteFile: true);
        var applied = (await context.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        if (applied.Count == 0)
        {
            var catalog = await ReadCatalogAsync(context, ct).ConfigureAwait(false);
            var tables = catalog.Tables.ToList();
            if (tables.Contains("alembic_version"))
            {
                await output.WriteLineAsync(
                    "This database was created by Alembic (the Python backend). Do not migrate it: run 'ef-baseline' " +
                    "to adopt it as it is.").ConfigureAwait(false);
                return 2;
            }

            if (tables.Count > 0)
            {
                await output.WriteLineAsync(
                    $"The database is not empty ({tables.Count} tables) and has no EF migration history: refusing to " +
                    "create the schema on top of it.").ConfigureAwait(false);
                return 2;
            }
        }

        var pending = (await context.Database.GetPendingMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        if (pending.Count == 0)
        {
            await output.WriteLineAsync($"{target.Description}: up to date.").ConfigureAwait(false);
            return 0;
        }

        await context.Database.MigrateAsync(ct).ConfigureAwait(false);
        await output.WriteLineAsync($"{target.Description}: applied {string.Join(", ", pending)}.").ConfigureAwait(false);
        return 0;
    }

    public async Task<int> BaselineAsync(CancellationToken ct = default)
    {
        await using var context = AtsDbContextOptions.CreateMigrationContext(target);
        var baseline = context.Database.GetMigrations().First();
        var applied = (await context.Database.GetAppliedMigrationsAsync(ct).ConfigureAwait(false)).ToList();
        if (applied.Contains(baseline))
        {
            await output.WriteLineAsync($"{target.Description}: already adopted ({baseline}).").ConfigureAwait(false);
            return 0;
        }

        var version = await AlembicVersionAsync(context, ct).ConfigureAwait(false);
        if (version != AlembicHead)
        {
            await output.WriteLineAsync(version is null
                ? "No alembic_version table: this is not a database created by the Python backend. Use 'migrate' for a new database."
                : $"Alembic version is {version}, expected {AlembicHead}: upgrade it with the Python backend first " +
                  "(alembic upgrade head).").ConfigureAwait(false);
            return 2;
        }

        if (!await MatchesAsync(context, ct).ConfigureAwait(false))
        {
            await output.WriteLineAsync("Schema differs from Alembic head 0006: nothing was changed.").ConfigureAwait(false);
            return 2;
        }

        var history = context.GetService<IHistoryRepository>();
        await using var transaction = await context.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync(history.GetCreateIfNotExistsScript(), ct).ConfigureAwait(false);
        await context.Database.ExecuteSqlRawAsync(
            history.GetInsertScript(new HistoryRow(baseline, ProductInfo.GetVersion())), ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        await output.WriteLineAsync(
            $"{target.Description}: schema matches Alembic head {AlembicHead}; recorded {baseline} as applied " +
            "(only __EFMigrationsHistory was added).").ConfigureAwait(false);
        return 0;
    }

    public async Task<int> CheckAsync(CancellationToken ct = default)
    {
        await using var context = AtsDbContextOptions.CreateMigrationContext(target);
        return await MatchesAsync(context, ct).ConfigureAwait(false) ? 0 : 1;
    }

    public async Task<int> CatalogAsync(string? outFile, CancellationToken ct = default)
    {
        await using var context = AtsDbContextOptions.CreateMigrationContext(target);
        var catalog = await ReadCatalogAsync(context, ct).ConfigureAwait(false);
        var json = catalog.ToJson(Environment.GetEnvironmentVariable("ATS_CATALOG_SOURCE") ?? $"schema-catalog of a {catalog.Provider} database");
        if (outFile is null)
        {
            await output.WriteAsync(json).ConfigureAwait(false);
        }
        else
        {
            await File.WriteAllTextAsync(outFile, json, ct).ConfigureAwait(false);
            await output.WriteLineAsync($"{catalog.Entries.Count} entries written to {outFile}").ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<bool> MatchesAsync(AtsDbContext context, CancellationToken ct)
    {
        var actual = await ReadCatalogAsync(context, ct).ConfigureAwait(false);
        var (missing, unexpected) = actual.CompareTo(SchemaCatalog.Expected(target.Provider));
        if (missing.Count == 0 && unexpected.Count == 0)
        {
            await output.WriteLineAsync($"{target.Description}: schema matches Alembic head {AlembicHead} " +
                                        $"({actual.Entries.Count} entries).").ConfigureAwait(false);
            return true;
        }

        foreach (var line in missing.Take(40))
        {
            await output.WriteLineAsync("  missing:    " + line).ConfigureAwait(false);
        }

        foreach (var line in unexpected.Take(40))
        {
            await output.WriteLineAsync("  unexpected: " + line).ConfigureAwait(false);
        }

        await output.WriteLineAsync($"{missing.Count} missing, {unexpected.Count} unexpected.").ConfigureAwait(false);
        return false;
    }

    private async Task<SchemaCatalog> ReadCatalogAsync(AtsDbContext context, CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            return await SchemaCatalog.ReadAsync(connection, target.Provider, ct).ConfigureAwait(false);
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }

    private static async Task<string?> AlembicVersionAsync(AtsDbContext context, CancellationToken ct)
    {
        var connection = context.Database.GetDbConnection();
        await context.Database.OpenConnectionAsync(ct).ConfigureAwait(false);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT version_num FROM alembic_version";
            return await command.ExecuteScalarAsync(ct).ConfigureAwait(false) as string;
        }
        catch (System.Data.Common.DbException)
        {
            return null; // no alembic_version table
        }
        finally
        {
            await context.Database.CloseConnectionAsync().ConfigureAwait(false);
        }
    }
}
