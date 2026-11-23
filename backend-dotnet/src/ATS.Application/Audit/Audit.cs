using System.Collections;
using System.Globalization;
using System.Text.Json.Nodes;
using Ats.Application.Common;
using Ats.Application.Data;
using Ats.Application.Python;
using Ats.Domain;
using Ats.Domain.Entities;

namespace Ats.Application.Audit;

/// <summary>
/// The audit trail (<c>app/services/audit.py</c>). Credentials and secrets are stripped from the values, whatever
/// the caller passes; enums are stored as their text and dates as ISO strings.
/// </summary>
public static class AuditTrail
{
    private static readonly HashSet<string> ForbiddenKeys =
        new(["password", "password_hash", "token", "token_hash", "csrf_token", "secret"], StringComparer.Ordinal);

    /// <summary>Adds an audit row to the unit of work (the caller saves).</summary>
    public static AuditLog Record(IAtsDb db, int? actorId, string action, string entityType, object? entityId = null,
                                  IReadOnlyDictionary<string, object?>? before = null,
                                  IReadOnlyDictionary<string, object?>? after = null, RequestContext? ctx = null)
    {
        ArgumentNullException.ThrowIfNull(db);
        var entry = new AuditLog
        {
            ActorUserId = actorId,
            Action = action,
            EntityType = entityType,
            EntityId = entityId is null ? null : Convert.ToString(entityId, CultureInfo.InvariantCulture),
            BeforeValues = before is null ? null : (JsonObject?)Clean(before),
            AfterValues = after is null ? null : (JsonObject?)Clean(after),
            RequestId = ctx?.RequestId,
            IpAddress = ctx?.IpAddress,
        };
        db.AuditLogs.Add(entry);
        return entry;
    }

    /// <summary>Only the keys whose values differ, as (old values, new values).</summary>
    public static (Dictionary<string, object?> Before, Dictionary<string, object?> After) Changes(
        IReadOnlyDictionary<string, object?> before, IReadOnlyDictionary<string, object?> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        // Values compare as Python's == does: lists by their items (the JSON form), not by reference.
        static string? Key(object? v) => Clean(v)?.ToJsonString();
        var keys = after.Keys.Where(k => Key(before.GetValueOrDefault(k)) != Key(after.GetValueOrDefault(k))).ToList();
        return (keys.ToDictionary(k => k, k => before.GetValueOrDefault(k)), keys.ToDictionary(k => k, k => after.GetValueOrDefault(k)));
    }

    internal static JsonNode? Clean(object? value) => value switch
    {
        null => null,
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        double d => JsonValue.Create(d),
        decimal m => JsonValue.Create(m),
        Enum e => JsonValue.Create(EnumText.Of(e)),
        DateTimeOffset t => JsonValue.Create(PyFormat.IsoDateTime(t)),
        DateOnly d => JsonValue.Create(PyFormat.IsoDate(d)),
        TimeOnly t => JsonValue.Create(PyFormat.IsoTime(t)),
        JsonNode node => node.DeepClone(),
        IReadOnlyDictionary<string, object?> map => CleanMap(map),
        IDictionary map => CleanMap(map.Cast<DictionaryEntry>().ToDictionary(e => (string)e.Key, e => e.Value)),
        IEnumerable items => new JsonArray([.. items.Cast<object?>().Select(Clean)]),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };

    private static JsonObject CleanMap(IReadOnlyDictionary<string, object?> map)
    {
        var obj = new JsonObject();
        foreach (var (key, v) in map)
        {
            if (!ForbiddenKeys.Contains(key))
            {
                obj[key] = Clean(v);
            }
        }

        return obj;
    }
}
