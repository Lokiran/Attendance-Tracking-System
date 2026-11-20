using Ats.Application.Common;
using Ats.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Ats.Infrastructure.Persistence;

/// <summary>
/// The same model per provider, so each provider has its own migration history
/// (<c>Persistence/Migrations/PostgreSql</c>, <c>Persistence/Migrations/Sqlite</c>). Used for migrations and the
/// database commands; the API uses <see cref="AtsDbContext"/> directly.
/// </summary>
public sealed class PostgresAtsDbContext(DbContextOptions<PostgresAtsDbContext> options, IClock? clock = null)
    : AtsDbContext(options, clock);

/// <inheritdoc cref="PostgresAtsDbContext"/>
public sealed class SqliteAtsDbContext(DbContextOptions<SqliteAtsDbContext> options, IClock? clock = null)
    : AtsDbContext(options, clock);

/// <summary>EF caches one model per context type; ours also differs by provider (column types, converters).</summary>
internal sealed class ProviderAwareModelCacheKeyFactory : IModelCacheKeyFactory
{
    public object Create(DbContext context, bool designTime) =>
        (context.GetType(), context.Database.ProviderName, designTime);
}

public static class AtsDbContextOptions
{
    /// <summary>Options for the configured database (never a fallback: the target says which provider).</summary>
    public static void Configure(DbContextOptionsBuilder options, DatabaseTarget target, bool allowCreateSqliteFile = false)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(target);
        if (target.Provider == DatabaseProvider.PostgreSql)
        {
            options.UseNpgsql(target.ConnectionString, npgsql =>
            {
                npgsql.MigrationsAssembly(typeof(AtsDbContext).Assembly.GetName().Name);
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory");
            });
        }
        else
        {
            var connection = allowCreateSqliteFile
                ? target.ConnectionString.Replace("Mode=ReadWrite", "Mode=ReadWriteCreate", StringComparison.Ordinal)
                : target.ConnectionString;
            options.UseSqlite(connection, sqlite =>
            {
                sqlite.MigrationsAssembly(typeof(AtsDbContext).Assembly.GetName().Name);
                sqlite.MigrationsHistoryTable("__EFMigrationsHistory");
            });
        }

        options.ReplaceService<IModelCacheKeyFactory, ProviderAwareModelCacheKeyFactory>();
    }

    /// <summary>The provider-specific context (migrations, database commands).</summary>
    public static AtsDbContext CreateMigrationContext(DatabaseTarget target, bool allowCreateSqliteFile = false)
    {
        ArgumentNullException.ThrowIfNull(target);
        if (target.Provider == DatabaseProvider.PostgreSql)
        {
            var b = new DbContextOptionsBuilder<PostgresAtsDbContext>();
            Configure(b, target);
            return new PostgresAtsDbContext(b.Options);
        }

        var s = new DbContextOptionsBuilder<SqliteAtsDbContext>();
        Configure(s, target, allowCreateSqliteFile);
        return new SqliteAtsDbContext(s.Options);
    }
}

/// <summary><c>dotnet ef migrations add … --context PostgresAtsDbContext</c> (no connection is opened).</summary>
public sealed class PostgresDesignTimeFactory : IDesignTimeDbContextFactory<PostgresAtsDbContext>
{
    public PostgresAtsDbContext CreateDbContext(string[] args) =>
        (PostgresAtsDbContext)AtsDbContextOptions.CreateMigrationContext(
            DatabaseTarget.Parse("postgresql://design@localhost:5432/ats_design", "."));
}

/// <summary><c>dotnet ef migrations add … --context SqliteAtsDbContext</c> (no connection is opened).</summary>
public sealed class SqliteDesignTimeFactory : IDesignTimeDbContextFactory<SqliteAtsDbContext>
{
    public SqliteAtsDbContext CreateDbContext(string[] args) =>
        (SqliteAtsDbContext)AtsDbContextOptions.CreateMigrationContext(
            DatabaseTarget.Parse("sqlite:///design.sqlite3", Path.GetTempPath()));
}
