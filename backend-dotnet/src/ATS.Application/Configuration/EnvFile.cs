using System.Text;
using System.Text.RegularExpressions;

namespace Ats.Application.Configuration;

/// <summary>
/// Reads a <c>.env</c> file the way python-dotenv does for pydantic-settings, so the Python backend's env files
/// load unchanged: <c>KEY=value</c> lines, optional <c>export </c>, comments, single quotes (literal),
/// double quotes (with <c>\n \t \" \\</c> escapes), inline <c> # comments</c> after unquoted values, and
/// <c>${NAME}</c> / <c>${NAME:-default}</c> expansion in unquoted and double-quoted values (earlier keys of the
/// file first, then the process environment).
/// </summary>
public static partial class EnvFile
{
    public static Dictionary<string, string> Parse(string text, Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#')
            {
                continue;
            }

            if (line.StartsWith("export ", StringComparison.Ordinal))
            {
                line = line["export ".Length..].TrimStart();
            }

            var eq = line.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue; // python-dotenv ignores lines it cannot parse
            }

            var key = line[..eq].Trim();
            var rest = line[(eq + 1)..].TrimStart();
            string value;
            var expand = true;
            if (rest.Length > 0 && (rest[0] == '"' || rest[0] == '\''))
            {
                var quote = rest[0];
                var end = FindClosingQuote(rest, quote);
                var inner = end > 0 ? rest[1..end] : rest[1..];
                if (quote == '\'')
                {
                    value = inner;
                    expand = false;
                }
                else
                {
                    value = Unescape(inner);
                }
            }
            else
            {
                value = StripInlineComment(rest).TrimEnd();
            }

            values[key] = expand ? Expand(value, values, environment) : value;
        }

        return values;
    }

    public static Dictionary<string, string> Load(string path) =>
        Parse(File.ReadAllText(path, Encoding.UTF8));

    private static int FindClosingQuote(string s, char quote)
    {
        for (var i = 1; i < s.Length; i++)
        {
            if (s[i] == '\\' && quote == '"')
            {
                i++;
                continue;
            }

            if (s[i] == quote)
            {
                return i;
            }
        }

        return -1;
    }

    private static string Unescape(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                var next = s[++i];
                sb.Append(next switch
                {
                    'n' => '\n',
                    't' => '\t',
                    'r' => '\r',
                    _ => next,
                });
            }
            else
            {
                sb.Append(s[i]);
            }
        }

        return sb.ToString();
    }

    private static string StripInlineComment(string s)
    {
        for (var i = 1; i < s.Length; i++)
        {
            if (s[i] == '#' && char.IsWhiteSpace(s[i - 1]))
            {
                return s[..i];
            }
        }

        return s;
    }

    private static string Expand(string value, Dictionary<string, string> earlier, Func<string, string?> environment) =>
        VariablePattern().Replace(value, m =>
        {
            var name = m.Groups["name"].Value;
            if (earlier.TryGetValue(name, out var v) || (v = environment(name)) is not null)
            {
                return v;
            }

            return m.Groups["default"].Success ? m.Groups["default"].Value : "";
        });

    [GeneratedRegex(@"\$\{(?<name>[^}:]+)(?::-(?<default>[^}]*))?\}")]
    private static partial Regex VariablePattern();
}

/// <summary>Where the backend's files are: the env file in use and the base directory for relative paths.</summary>
public sealed record EnvironmentInfo(string? EnvFile, string BaseDirectory, IReadOnlyDictionary<string, string> Values)
{
    /// <summary>
    /// <c>ATS_ENV_FILE</c> if set (absolute, or relative to the backend directory; it must exist), otherwise
    /// <c>&lt;backend-dotnet&gt;/.env</c> when it exists, otherwise no file (environment variables only).
    /// The base directory for relative paths in the settings is the env file's directory, so a file shared with the
    /// Python backend (for example <c>backend/.env.sqlite</c>) resolves <c>sqlite:///data/...</c> to the same file.
    /// </summary>
    public static EnvironmentInfo Select(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var root = BackendDirectory(environment);
        var requested = environment("ATS_ENV_FILE")?.Trim();
        string? path = null;
        if (!string.IsNullOrEmpty(requested))
        {
            path = Path.IsPathRooted(requested) ? requested : Path.GetFullPath(Path.Combine(root, requested));
            if (!File.Exists(path))
            {
                throw new SettingsException($"ATS_ENV_FILE points to '{path}', which does not exist");
            }
        }
        else if (File.Exists(Path.Combine(root, ".env")))
        {
            path = Path.Combine(root, ".env");
        }

        if (path is null)
        {
            return new EnvironmentInfo(null, root, new Dictionary<string, string>());
        }

        return new EnvironmentInfo(path, Path.GetDirectoryName(path) ?? root,
                                   Ats.Application.Configuration.EnvFile.Load(path));
    }

    /// <summary>
    /// <c>ATS_HOME</c> if set (containers), else the nearest directory above the app that holds <c>ATS.sln</c>
    /// (a source checkout), else the app's own directory.
    /// </summary>
    public static string BackendDirectory(Func<string, string?>? environment = null)
    {
        environment ??= Environment.GetEnvironmentVariable;
        var home = environment("ATS_HOME")?.Trim();
        if (!string.IsNullOrEmpty(home))
        {
            return Path.GetFullPath(home);
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "ATS.sln")))
                {
                    return dir.FullName;
                }
            }
        }

        return AppContext.BaseDirectory;
    }
}
