using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ats.Application.Json;

/// <summary>
/// JSON options that reproduce the FastAPI / Pydantic wire format (docs/dotnet-migration-plan.md §4):
/// snake_case properties, enums as their string values, nulls always written, UTC datetimes as
/// <c>2026-11-19T08:39:37.577436Z</c> (6 fraction digits, omitted when zero) and times as <c>09:30:00</c>.
/// </summary>
public static class AtsJson
{
    public static void Configure(JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
        options.DictionaryKeyPolicy = null;   // stored JSON (upload summaries, audit values) keeps its own keys
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.NumberHandling = JsonNumberHandling.AllowReadingFromString; // Pydantic's lax "5" -> 5
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping; // like json.dumps(ensure_ascii=False)
        options.Converters.Add(new UtcDateTimeOffsetConverter());
        options.Converters.Add(new PythonTimeConverter());
        options.Converters.Add(new JsonStringEnumConverter(namingPolicy: null, allowIntegerValues: false));
    }

    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions();
        Configure(options);
        return options;
    }
}

/// <summary>Pydantic's datetime format for aware UTC values.</summary>
public sealed class UtcDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public static string Format(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        var micro = utc.Ticks % TimeSpan.TicksPerSecond / 10;
        var text = utc.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
        return micro == 0 ? text + "Z" : $"{text}.{micro.ToString("D6", CultureInfo.InvariantCulture)}Z";
    }

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? throw new JsonException("Expected a datetime string");
        return DateTimeOffset.Parse(text, CultureInfo.InvariantCulture,
                                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Format(value));
    }
}

/// <summary>Python's <c>time.isoformat()</c>: <c>HH:MM:SS</c>, plus <c>.ffffff</c> when there are microseconds.</summary>
public sealed class PythonTimeConverter : JsonConverter<TimeOnly>
{
    public static string Format(TimeOnly value)
    {
        var micro = value.Ticks % TimeSpan.TicksPerSecond / 10;
        var text = value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        return micro == 0 ? text : $"{text}.{micro.ToString("D6", CultureInfo.InvariantCulture)}";
    }

    public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var text = reader.GetString() ?? throw new JsonException("Expected a time string");
        return TimeOnly.Parse(text, CultureInfo.InvariantCulture);
    }

    public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(Format(value));
    }
}
