using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Ats.Domain;

/// <summary>An enum member's stored text ("OFFICE", "MANUAL_OVERRIDE"), from its <see cref="JsonStringEnumMemberNameAttribute"/>.</summary>
public static class EnumText
{
    private static readonly ConcurrentDictionary<Type, (Dictionary<Enum, string> ToText, Dictionary<string, Enum> FromText)> Maps = new();

    public static string Of(Enum value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Map(value.GetType()).ToText[value];
    }

    public static TEnum Parse<TEnum>(string text)
        where TEnum : struct, Enum =>
        Map(typeof(TEnum)).FromText.TryGetValue(text, out var v)
            ? (TEnum)v
            : throw new InvalidOperationException($"Unknown {typeof(TEnum).Name} value '{text}'");

    public static bool TryParse<TEnum>(string? text, out TEnum value)
        where TEnum : struct, Enum
    {
        if (text is not null && Map(typeof(TEnum)).FromText.TryGetValue(text, out var v))
        {
            value = (TEnum)v;
            return true;
        }

        value = default;
        return false;
    }

    public static IReadOnlyCollection<string> All<TEnum>()
        where TEnum : struct, Enum => Map(typeof(TEnum)).ToText.Values;

    private static (Dictionary<Enum, string> ToText, Dictionary<string, Enum> FromText) Map(Type type) =>
        Maps.GetOrAdd(type, t =>
        {
            var toText = Enum.GetValues(t).Cast<Enum>().ToDictionary(
                v => v,
                v => t.GetField(v.ToString())!.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
                     ?? throw new InvalidOperationException($"{t.Name}.{v} has no stored name"));
            return (toText, toText.ToDictionary(kv => kv.Value, kv => kv.Key, StringComparer.Ordinal));
        });
}
