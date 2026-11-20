using System.Data.Common;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ats.Infrastructure.Database;

namespace Ats.Infrastructure.Persistence.Schema;

/// <summary>
/// A database schema as sorted, normalised lines (tables, columns, constraints, indexes, sequences, extensions),
/// so two databases can be compared exactly. The expected catalogs (<c>expected-postgresql.json</c>,
/// <c>expected-sqlite.json</c>) were read from databases built by <c>alembic upgrade head</c> (0006).
/// EF Core's own <c>__EFMigrationsHistory</c> / <c>__EFMigrationsLock</c> tables are ignored.
/// </summary>
public sealed partial record SchemaCatalog(string Provider, IReadOnlyList<string> Entries)
{
    public const string HistoryTable = "__EFMigrationsHistory";

    /// <summary>EF Core's migration lock table (SQLite), also bookkeeping rather than application schema.</summary>
    public const string LockTable = "__EFMigrationsLock";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // readable SQL in the file
    };

    public static async Task<SchemaCatalog> ReadAsync(DbConnection connection, DatabaseProvider provider,
                                                      CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        var entries = provider == DatabaseProvider.PostgreSql
            ? await ReadPostgresAsync(connection, cancellationToken).ConfigureAwait(false)
            : await ReadSqliteAsync(connection, cancellationToken).ConfigureAwait(false);
        entries.Sort(StringComparer.Ordinal);
        return new SchemaCatalog(provider == DatabaseProvider.PostgreSql ? "postgresql" : "sqlite", entries);
    }

    /// <summary>The schema Alembic creates at head 0006, for the provider.</summary>
    public static SchemaCatalog Expected(DatabaseProvider provider)
    {
        var file = provider == DatabaseProvider.PostgreSql ? "expected-postgresql.json" : "expected-sqlite.json";
        var assembly = typeof(SchemaCatalog).Assembly;
        var name = assembly.GetManifestResourceNames().Single(n => n.EndsWith("." + file, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(name)!;
        return FromJson(new StreamReader(stream).ReadToEnd());
    }

    public static SchemaCatalog FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        return new SchemaCatalog(root.GetProperty("provider").GetString()!,
                                 root.GetProperty("entries").EnumerateArray().Select(e => e.GetString()!).ToList());
    }

    public string ToJson(string source) =>
        JsonSerializer.Serialize(new { provider = Provider, source, entries = Entries }, JsonOptions) + "\n";

    /// <summary>Lines only in the expected schema (missing) and only in this one (unexpected).</summary>
    public (IReadOnlyList<string> Missing, IReadOnlyList<string> Unexpected) CompareTo(SchemaCatalog expected)
    {
        ArgumentNullException.ThrowIfNull(expected);
        var mine = Entries.ToHashSet(StringComparer.Ordinal);
        var theirs = expected.Entries.ToHashSet(StringComparer.Ordinal);
        return (expected.Entries.Where(e => !mine.Contains(e)).ToList(), Entries.Where(e => !theirs.Contains(e)).ToList());
    }

    public IEnumerable<string> Tables => Entries.Where(e => e.StartsWith("table ", StringComparison.Ordinal))
        .Select(e => e.Split(' ', 3)[1]);

    private static async Task<List<string>> ReadPostgresAsync(DbConnection c, CancellationToken ct)
    {
        var entries = new List<string>();
        await AddRows(c, entries, "SELECT extname FROM pg_extension WHERE extname <> 'plpgsql'", r => $"extension {r[0]}", ct).ConfigureAwait(false);
        await AddRows(c, entries,
            "SELECT table_name FROM information_schema.tables WHERE table_schema = 'public' AND table_type = 'BASE TABLE' " +
            $"AND table_name NOT IN ('{HistoryTable}', '{LockTable}')", r => $"table {r[0]}", ct).ConfigureAwait(false);
        await AddRows(c, entries,
            "SELECT table_name, column_name, " +
            "CASE WHEN character_maximum_length IS NOT NULL THEN data_type || '(' || character_maximum_length || ')' ELSE data_type END, " +
            "is_nullable, COALESCE(column_default, '') FROM information_schema.columns " +
            $"WHERE table_schema = 'public' AND table_name NOT IN ('{HistoryTable}', '{LockTable}')",
            r => $"column {r[0]}.{r[1]} {r[2]} {(r[3] == "YES" ? "NULL" : "NOT NULL")}" + (r[4].Length > 0 ? $" DEFAULT {r[4]}" : ""), ct).ConfigureAwait(false);
        await AddRows(c, entries,
            "SELECT rel.relname, con.conname, pg_get_constraintdef(con.oid) FROM pg_constraint con " +
            "JOIN pg_class rel ON rel.oid = con.conrelid JOIN pg_namespace n ON n.oid = rel.relnamespace " +
            $"WHERE n.nspname = 'public' AND rel.relname NOT IN ('{HistoryTable}', '{LockTable}')",
            r => $"constraint {r[0]}.{r[1]} {r[2]}", ct).ConfigureAwait(false);
        await AddRows(c, entries,
            $"SELECT tablename, indexname, indexdef FROM pg_indexes WHERE schemaname = 'public' AND tablename NOT IN ('{HistoryTable}', '{LockTable}')",
            r => $"index {r[0]}.{r[1]} {r[2]}", ct).ConfigureAwait(false);
        await AddRows(c, entries,
            "SELECT sequence_name, data_type FROM information_schema.sequences WHERE sequence_schema = 'public'",
            r => $"sequence {r[0]} {r[1]}", ct).ConfigureAwait(false);
        return entries;
    }

    private static async Task<List<string>> ReadSqliteAsync(DbConnection c, CancellationToken ct)
    {
        var entries = new List<string>();
        await AddRows(c, entries,
            "SELECT type, name, sql FROM sqlite_master WHERE sql IS NOT NULL AND name NOT LIKE 'sqlite_%' " +
            $"AND tbl_name NOT IN ('{HistoryTable}', '{LockTable}')",
            r => $"{r[0]} {r[1]} {Normalize(r[2])}", ct).ConfigureAwait(false);
        return entries;
    }

    private static async Task AddRows(DbConnection connection, List<string> entries, string sql,
                                      Func<string[], string> line, CancellationToken ct)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            var values = new string[reader.FieldCount];
            for (var i = 0; i < values.Length; i++)
            {
                values[i] = reader.IsDBNull(i) ? "" : Convert.ToString(reader.GetValue(i), System.Globalization.CultureInfo.InvariantCulture)!;
            }

            entries.Add(line(values));
        }
    }

    /// <summary>
    /// One line with single spaces (SQLite keeps CREATE statements as written). In CREATE TABLE the columns keep
    /// their order and the table-level constraints are sorted: Alembic's batch mode rebuilds tables with the
    /// constraints in reflection order, which varies between databases and means nothing to SQLite.
    /// </summary>
    internal static string Normalize(string sql)
    {
        var text = Whitespace().Replace(sql, " ").Trim();
        var open = text.IndexOf('(', StringComparison.Ordinal);
        if (!text.StartsWith("CREATE TABLE", StringComparison.OrdinalIgnoreCase) || open < 0 || !text.EndsWith(')'))
        {
            return text;
        }

        var parts = SplitTopLevel(text[(open + 1)..^1]);
        var columns = parts.Where(p => !IsTableConstraint(p));
        var constraints = parts.Where(IsTableConstraint).Order(StringComparer.Ordinal);
        return $"{text[..open].TrimEnd()} ( {string.Join(", ", columns.Concat(constraints))} )";
    }

    private static bool IsTableConstraint(string part) =>
        part.StartsWith("CONSTRAINT ", StringComparison.OrdinalIgnoreCase)
        || part.StartsWith("PRIMARY KEY", StringComparison.OrdinalIgnoreCase)
        || part.StartsWith("UNIQUE", StringComparison.OrdinalIgnoreCase)
        || part.StartsWith("CHECK", StringComparison.OrdinalIgnoreCase)
        || part.StartsWith("FOREIGN KEY", StringComparison.OrdinalIgnoreCase);

    /// <summary>Splits on commas outside parentheses and quotes.</summary>
    private static List<string> SplitTopLevel(string body)
    {
        var parts = new List<string>();
        var depth = 0;
        char? quote = null;
        var start = 0;
        for (var i = 0; i < body.Length; i++)
        {
            var c = body[i];
            if (quote is not null)
            {
                if (c == quote)
                {
                    quote = null;
                }
            }
            else if (c is '\'' or '"')
            {
                quote = c;
            }
            else if (c == '(')
            {
                depth++;
            }
            else if (c == ')')
            {
                depth--;
            }
            else if (c == ',' && depth == 0)
            {
                parts.Add(body[start..i].Trim());
                start = i + 1;
            }
        }

        parts.Add(body[start..].Trim());
        return parts;
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}
