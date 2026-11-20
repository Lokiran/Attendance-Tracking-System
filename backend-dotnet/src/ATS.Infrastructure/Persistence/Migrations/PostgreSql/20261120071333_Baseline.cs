using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Ats.Infrastructure.Persistence.Migrations.PostgreSql
{
    /// <summary>
    /// The Python backend's schema at Alembic head 0006, created with Alembic's exact DDL (Baseline.postgresql.sql).
    /// Existing databases are not migrated: `ef-baseline` checks them and records this migration as applied.
    /// </summary>
    public partial class Baseline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(MigrationSql.Load("Baseline.postgresql.sql"));

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            migrationBuilder.Sql(string.Join("\n", MigrationSql.Tables.Select(t => $"DROP TABLE IF EXISTS public.{t} CASCADE;")));
    }
}
