using System.Text.RegularExpressions;
using Ats.Infrastructure.Database;
using Ats.Infrastructure.Persistence;
using Ats.Infrastructure.Persistence.Schema;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Ats.Tests.Persistence;

/// <summary>The EF model maps every table and column of the Alembic schema, with the same types and keys.</summary>
public sealed partial class ModelMappingTests
{
    private sealed record Column(string Type, bool Nullable);

    private static readonly SchemaCatalog Expected = SchemaCatalog.Expected(DatabaseProvider.PostgreSql);

    private static Dictionary<(string Table, string Column), Column> ExpectedColumns() =>
        Expected.Entries.Select(e => ColumnLine().Match(e)).Where(m => m.Success).ToDictionary(
            m => (m.Groups["table"].Value, m.Groups["column"].Value),
            m => new Column(m.Groups["type"].Value, m.Groups["null"].Value == "NULL"));

    private static IModel PostgresModel()
    {
        using var context = new PostgresDesignTimeFactory().CreateDbContext([]);
        return context.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model;
    }

    [Fact]
    public void Every_property_maps_to_an_alembic_column_with_the_same_type_and_nullability()
    {
        var expected = ExpectedColumns();
        var problems = new List<string>();
        foreach (var entity in PostgresModel().GetEntityTypes())
        {
            var table = entity.GetTableName()!;
            var store = StoreObjectIdentifier.Table(table);
            foreach (var property in entity.GetProperties())
            {
                var column = property.GetColumnName(store)!;
                if (!expected.TryGetValue((table, column), out var db))
                {
                    problems.Add($"{table}.{column}: not in the Alembic schema");
                    continue;
                }

                if (property.IsColumnNullable(store) != db.Nullable)
                {
                    problems.Add($"{table}.{column}: nullable {property.IsColumnNullable(store)} vs {db.Nullable}");
                }

                if (property.GetColumnType(store) != db.Type)
                {
                    problems.Add($"{table}.{column}: type {property.GetColumnType(store)} vs {db.Type}");
                }
            }
        }

        problems.Should().BeEmpty();
    }

    [Fact]
    public void Every_alembic_column_is_mapped()
    {
        var mapped = PostgresModel().GetEntityTypes()
            .SelectMany(e => e.GetProperties().Select(p =>
                (e.GetTableName()!, p.GetColumnName(StoreObjectIdentifier.Table(e.GetTableName()!))!)))
            .ToHashSet();
        ExpectedColumns().Keys.Where(k => k.Table != "alembic_version" && !mapped.Contains(k))
            .Should().BeEmpty("every column of the schema must be reachable from the .NET model");
    }

    [Fact]
    public void Foreign_keys_have_the_alembic_names_and_delete_rules()
    {
        var constraints = Expected.Entries.Where(e => e.StartsWith("constraint ", StringComparison.Ordinal)).ToList();
        var problems = new List<string>();
        foreach (var entity in PostgresModel().GetEntityTypes())
        {
            foreach (var fk in entity.GetForeignKeys())
            {
                var name = fk.GetConstraintName()!;
                var line = constraints.FirstOrDefault(c => c.StartsWith($"constraint {entity.GetTableName()}.{name} FOREIGN KEY", StringComparison.Ordinal));
                if (line is null)
                {
                    problems.Add($"{name}: not in the Alembic schema");
                    continue;
                }

                var rule = fk.DeleteBehavior switch
                {
                    DeleteBehavior.Cascade => "ON DELETE CASCADE",
                    DeleteBehavior.SetNull => "ON DELETE SET NULL",
                    _ => "?",
                };
                if (!line.EndsWith(rule, StringComparison.Ordinal))
                {
                    problems.Add($"{name}: {fk.DeleteBehavior} vs {line}");
                }
            }
        }

        problems.Should().BeEmpty();
        var modelFks = PostgresModel().GetEntityTypes().SelectMany(e => e.GetForeignKeys()).Count();
        modelFks.Should().Be(constraints.Count(c => c.Contains(" FOREIGN KEY ", StringComparison.Ordinal)));
    }

    [Fact]
    public void Primary_keys_and_indexes_use_the_alembic_names()
    {
        var names = Expected.Entries.Select(e => e.Split(' ')[1]).ToHashSet();
        foreach (var entity in PostgresModel().GetEntityTypes())
        {
            names.Should().Contain($"{entity.GetTableName()}.{entity.FindPrimaryKey()!.GetName()}");
            foreach (var index in entity.GetIndexes())
            {
                names.Should().Contain($"{entity.GetTableName()}.{index.GetDatabaseName()}");
            }
        }
    }

    [GeneratedRegex(@"^column (?<table>\w+)\.(?<column>\w+) (?<type>.+?) (?<null>NULL|NOT NULL)(?: DEFAULT .*)?$")]
    private static partial Regex ColumnLine();
}
