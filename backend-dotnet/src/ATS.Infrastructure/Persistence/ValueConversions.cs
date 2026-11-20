using System.Globalization;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ats.Application.Python;
using Ats.Domain;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Ats.Infrastructure.Persistence;

/// <summary>EF converters between an enum and its stored text (see <see cref="EnumText"/>).</summary>
public static class EnumStrings
{
    public static string Text<TEnum>(TEnum value)
        where TEnum : struct, Enum => EnumText.Of(value);

    public static ValueConverter<TEnum, string> Converter<TEnum>()
        where TEnum : struct, Enum => new(v => EnumText.Of(v), s => EnumText.Parse<TEnum>(s));
}

/// <summary>Column formats that match what SQLAlchemy writes, so both backends can share one database.</summary>
internal static class StoredFormats
{
    // SQLAlchemy's SQLite DATETIME and TIME text: always six fraction digits, naive UTC.
    private const string SqliteDateTime = "yyyy-MM-dd HH:mm:ss.ffffff";
    private const string SqliteTime = "HH:mm:ss.ffffff";

    private static readonly string[] DateTimeInputs =
        ["yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm", "yyyy-MM-dd"];

    private static readonly string[] TimeInputs = ["HH:mm:ss.FFFFFFF", "HH:mm"];

    public static string ToSqlite(DateTimeOffset value) =>
        value.UtcDateTime.ToString(SqliteDateTime, CultureInfo.InvariantCulture);

    public static DateTimeOffset FromSqlite(string text)
    {
        var parsed = DateTime.ParseExact(text.Trim(), DateTimeInputs, CultureInfo.InvariantCulture,
                                         DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return new DateTimeOffset(parsed, TimeSpan.Zero);
    }

    public static string ToSqlite(TimeOnly value) => value.ToString(SqliteTime, CultureInfo.InvariantCulture);

    public static TimeOnly TimeFromSqlite(string text) =>
        TimeOnly.ParseExact(text.Trim(), TimeInputs, CultureInfo.InvariantCulture);

    public static ValueConverter<DateTimeOffset, string> SqliteDateTimeConverter { get; } =
        new(v => ToSqlite(v), s => FromSqlite(s));

    public static ValueConverter<TimeOnly, string> SqliteTimeConverter { get; } =
        new(v => ToSqlite(v), s => TimeFromSqlite(s));

    /// <summary>Npgsql only writes timestamptz from UTC offsets; values are normalised on the way in.</summary>
    public static ValueConverter<DateTimeOffset, DateTimeOffset> PostgresUtcConverter { get; } =
        new(v => v.ToUniversalTime(), v => v.ToUniversalTime());

    // ---- JSON columns (written like Python's json.dumps)

    public static ValueConverter<List<int>, string> IntListConverter { get; } =
        new(v => PyJson.Dumps(v), s => JsonSerializer.Deserialize<List<int>>(s, (JsonSerializerOptions?)null) ?? new List<int>());

    public static ValueConverter<List<string>, string> StringListConverter { get; } =
        new(v => PyJson.Dumps(v), s => JsonSerializer.Deserialize<List<string>>(s, (JsonSerializerOptions?)null) ?? new List<string>());

    /// <summary>
    /// A JSON object column. Python's SQLAlchemy JSON type stores a None it was given as the JSON text 'null' (and a
    /// column it was never given as SQL NULL); both read as null. .NET writes null as SQL NULL (EF's null-converting
    /// converters are an internal API); both backends read either form as null (plan: parity note P11).
    /// </summary>
    public static ValueConverter<JsonObject?, string?> ObjectConverter { get; } =
        new(v => v == null ? null : PyJson.Dumps(v), s => ParseObject(s));

    public static ValueComparer<List<int>> IntListComparer { get; } = ListComparer<int>();

    public static ValueComparer<List<string>> StringListComparer { get; } = ListComparer<string>();

    public static ValueComparer<JsonObject?> ObjectComparer { get; } = new(
        (a, b) => JsonNode.DeepEquals(a, b),
        v => v == null ? 0 : v.ToJsonString((JsonSerializerOptions?)null).GetHashCode(StringComparison.Ordinal),
        v => v == null ? null : (JsonObject)v.DeepClone());

    private static JsonObject? ParseObject(string? text) =>
        text is null ? null : JsonNode.Parse(text) switch
        {
            null => null, // the JSON text 'null'
            JsonObject obj => obj,
            var other => throw new InvalidOperationException($"Expected a JSON object, found {other.GetValueKind()}"),
        };

    private static ValueComparer<List<T>> ListComparer<T>()
    {
        Expression<Func<List<T>?, List<T>?, bool>> equals = (a, b) =>
            a == null ? b == null : b != null && a.SequenceEqual(b);
        Expression<Func<List<T>, int>> hash = v => v.Aggregate(0, (h, x) => HashCode.Combine(h, x));
        Expression<Func<List<T>, List<T>>> snapshot = v => v.ToList();
        return new ValueComparer<List<T>>(equals, hash, snapshot);
    }
}
