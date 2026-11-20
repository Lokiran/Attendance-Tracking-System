namespace Ats.Application.Configuration;

/// <summary>
/// Application settings. The keys, defaults and startup rules are those of the Python backend
/// (<c>backend/app/core/config.py</c>), so an existing <c>.env</c> works unchanged.
/// Built and validated by <see cref="SettingsLoader"/>.
/// </summary>
public sealed class AtsSettings
{
    public const string Development = "development";
    public const string Test = "test";
    public const string Production = "production";

    public string AppEnv { get; init; } = Development;

    public string AppName { get; init; } = "Office Attendance Tracker";

    /// <summary>Required; there is no default database.</summary>
    public string DatabaseUrl { get; init; } = "";

    public string SecretKey { get; init; } = "";

    /// <summary>True when no SECRET_KEY was configured outside production (sessions reset on restart).</summary>
    public bool SecretKeyIsEphemeral { get; init; }

    public string OfficeTimezone { get; init; } = "Asia/Kolkata";

    public TimeZoneInfo TimeZone { get; init; } = TimeZoneInfo.Utc;

    public int SessionIdleMinutes { get; init; } = 120;

    public int SessionAbsoluteHours { get; init; } = 12;

    public string SessionCookieName { get; init; } = "ats_session";

    public string CsrfCookieName { get; init; } = "ats_csrf";

    public string CsrfHeaderName { get; init; } = "X-CSRF-Token";

    /// <summary>Null: secure cookies in production only.</summary>
    public bool? CookieSecure { get; init; }

    /// <summary>lax | strict | none.</summary>
    public string CookieSameSite { get; init; } = "lax";

    public string? CookieDomain { get; init; }

    public string CorsOrigins { get; init; } = "http://localhost:5173";

    public int LoginMaxFailures { get; init; } = 5;

    public int LoginWindowMinutes { get; init; } = 15;

    public int LoginIpMaxFailures { get; init; } = 30;

    public bool TrustProxyHeaders { get; init; }

    public bool PasswordLoginEnabled { get; init; } = true;

    public string EntraTenantId { get; init; } = "";

    public string EntraClientId { get; init; } = "";

    public string EntraClientSecret { get; init; } = "";

    public string EntraRedirectUri { get; init; } = "";

    public string EntraAuthority { get; init; } = "https://login.microsoftonline.com";

    public bool DevLoginEnabled { get; init; }

    public int MaxReportDays { get; init; } = 366;

    public string ReportCompanyName { get; init; } = "";

    public string ReportLogoFile { get; init; } = "";

    public string ReportBrandColor { get; init; } = "#0F172A";

    public string ReportAccentColor { get; init; } = "#2563EB";

    public string LogLevel { get; init; } = "INFO";

    /// <summary>WFO_MAPPING_FILE (absolute, or relative to <see cref="BaseDirectory"/>); empty: the bundled file.</summary>
    public string WfoMappingFile { get; init; } = "";

    /// <summary>The env file that was loaded, if any (for the startup log line).</summary>
    public string? EnvFile { get; init; }

    /// <summary>Relative paths (SQLite file, logo, mapping file) are resolved against this directory.</summary>
    public string BaseDirectory { get; init; } = AppContext.BaseDirectory;

    public bool IsProduction => AppEnv == Production;

    /// <summary>Entra ID sign-in is on when all four values are set (Python: truthiness, not stripped).</summary>
    public bool EntraEnabled =>
        EntraTenantId.Length > 0 && EntraClientId.Length > 0 && EntraClientSecret.Length > 0
        && EntraRedirectUri.Length > 0;

    public bool SecureCookies => CookieSecure ?? IsProduction;

    public IReadOnlyList<string> CorsOriginList =>
        CorsOrigins.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>A path from the settings, made absolute against <see cref="BaseDirectory"/>.</summary>
    public string ResolvePath(string path) =>
        Path.IsPathRooted(path) ? path : Path.GetFullPath(Path.Combine(BaseDirectory, path));
}
