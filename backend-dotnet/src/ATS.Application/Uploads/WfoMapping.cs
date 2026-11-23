using System.Text.Json;
using System.Text.RegularExpressions;
using Ats.Application.Configuration;
using Ats.Application.Python;
using Ats.Domain;

namespace Ats.Application.Uploads;

/// <summary>
/// Accepted text values for timesheet uploads (<c>app/core/wfo_mapping.py</c>), loaded from a JSON file, never
/// hard-coded: the bundled <c>config/wfo_mapping.json</c>, or <c>WFO_MAPPING_FILE</c>. Values ignore case, extra spaces,
/// hyphens and underscores; column names also ignore punctuation ("Emp. Code" = "EMP_CODE").
/// </summary>
public sealed partial class WfoMapping
{
    public static readonly string[] ColumnKeys = ["employee", "name", "date", "work_mode", "hours", "in_time", "out_time", "source"];

    private readonly Dictionary<string, WorkMode> _workModes;
    private readonly HashSet<string> _badgeSources;
    private readonly Dictionary<string, string> _columns;

    private WfoMapping(Dictionary<string, WorkMode> workModes, HashSet<string> badgeSources, Dictionary<string, string> columns)
    {
        _workModes = workModes;
        _badgeSources = badgeSources;
        _columns = columns;
    }

    /// <summary><c>normalize</c>: lower case, runs of spaces / '_' / '-' become one space.</summary>
    public static string Normalize(string? text) =>
        PyText.Strip(Separators().Replace(PyText.Strip(text ?? "").ToLowerInvariant(), " "));

    /// <summary><c>normalize_header</c>: anything but a-z / 0-9 becomes a space.</summary>
    public static string NormalizeHeader(string? text) =>
        PyText.Strip(NotAlphanumeric().Replace((text ?? "").ToLowerInvariant(), " "));

    public WorkMode? WorkMode(string? text) => _workModes.TryGetValue(Normalize(text), out var m) ? m : null;

    public bool IsBadge(string? text) => _badgeSources.Contains(Normalize(text));

    public string? Column(string? header) => _columns.GetValueOrDefault(NormalizeHeader(header));

    /// <summary><c>parse_mapping</c>: unknown work modes / columns, or a value listed for two modes, are errors.</summary>
    public static WfoMapping Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var modes = new Dictionary<string, WorkMode>(StringComparer.Ordinal);
        if (root.TryGetProperty("work_modes", out var workModes) && workModes.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in workModes.EnumerateObject())
            {
                if (!EnumText.TryParse<WorkMode>(entry.Name, out var mode))
                {
                    throw new InvalidOperationException(
                        $"wfo_mapping: unknown work mode {PyFormat.Repr(entry.Name)} (use ['OFFICE', 'WFH', 'LEAVE', 'HOLIDAY', 'WEEKOFF'])");
                }

                foreach (var value in entry.Value.EnumerateArray())
                {
                    var text = value.GetString() ?? "";
                    var key = Normalize(text);
                    if (modes.TryGetValue(key, out var existing) && existing != mode)
                    {
                        throw new InvalidOperationException(
                            $"wfo_mapping: {PyFormat.Repr(text)} is listed for both {EnumText.Of(existing)} and {EnumText.Of(mode)}");
                    }

                    modes[key] = mode;
                }

                modes.TryAdd(Normalize(EnumText.Of(mode)), mode); // the canonical name always works
            }
        }

        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("columns", out var columnList) && columnList.ValueKind == JsonValueKind.Object)
        {
            foreach (var entry in columnList.EnumerateObject())
            {
                if (!ColumnKeys.Contains(entry.Name))
                {
                    throw new InvalidOperationException(
                        $"wfo_mapping: unknown column {PyFormat.Repr(entry.Name)} (use [{string.Join(", ", ColumnKeys.Select(PyFormat.Repr))}])");
                }

                columns.TryAdd(NormalizeHeader(entry.Name), entry.Name);
                foreach (var alias in entry.Value.EnumerateArray())
                {
                    columns.TryAdd(NormalizeHeader(alias.GetString()), entry.Name);
                }
            }
        }

        var badges = new HashSet<string>(StringComparer.Ordinal);
        if (root.TryGetProperty("badge_sources", out var sources) && sources.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sources.EnumerateArray())
            {
                badges.Add(Normalize(s.GetString()));
            }
        }

        return new WfoMapping(modes, badges, columns);
    }

    /// <summary>The mapping file in use: <c>WFO_MAPPING_FILE</c> (relative to the base directory), else the bundled one.</summary>
    public static string FileFor(AtsSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var raw = settings.WfoMappingFile.Trim();
        return raw.Length == 0 ? Path.Combine(AppContext.BaseDirectory, "config", "wfo_mapping.json") : settings.ResolvePath(raw);
    }

    public static WfoMapping Load(AtsSettings settings)
    {
        var path = FileFor(settings);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"WFO mapping file not found: {path}");
        }

        return Parse(File.ReadAllText(path));
    }

    [GeneratedRegex(@"[\s\x1c-\x1f_\-]+")]
    private static partial Regex Separators();

    [GeneratedRegex("[^0-9a-z]+")]
    private static partial Regex NotAlphanumeric();
}
