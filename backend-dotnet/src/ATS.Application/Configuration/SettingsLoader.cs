using System.Globalization;
using System.Security.Cryptography;

namespace Ats.Application.Configuration;

/// <summary>A setting is missing, malformed or unsafe: the app refuses to start.</summary>
public sealed class SettingsException(string message) : Exception(message)
{
    public SettingsException()
        : this("Invalid settings")
    {
    }

    public SettingsException(string message, Exception innerException)
        : this(message + ": " + innerException.Message)
    {
    }
}

/// <summary>
/// Builds <see cref="AtsSettings"/> from configuration values (environment variables override the env file),
/// parsing values the way pydantic-settings does and applying the same startup rules as
/// <c>Settings._validate</c> in <c>backend/app/core/config.py</c>.
/// </summary>
public static class SettingsLoader
{
    private static readonly string[] InsecureSecretMarkers = ["change_me", "change-me", "changeme", "secret", "password", "example"];
    private static readonly string[] AppEnvs = [AtsSettings.Development, AtsSettings.Test, AtsSettings.Production];
    private static readonly string[] SameSiteValues = ["lax", "strict", "none"];

    /// <param name="get">Looks up a key (case-insensitive), null when not set.</param>
    /// <param name="baseDirectory">Directory relative paths are resolved against.</param>
    /// <param name="envFile">The env file that was loaded, if any.</param>
    /// <param name="warn">Receives non-fatal warnings (logged by the caller).</param>
    public static AtsSettings Load(Func<string, string?> get, string baseDirectory, string? envFile = null,
                                   Action<string>? warn = null)
    {
        ArgumentNullException.ThrowIfNull(get);
        var r = new Reader(get);

        var databaseUrl = (r.Raw("DATABASE_URL") ?? "").Trim();
        if (databaseUrl.Length == 0)
        {
            throw new SettingsException(
                "DATABASE_URL is not set and there is no default database. Configure the env file " +
                "(PostgreSQL), or use the local SQLite profile: ATS_ENV_FILE pointing at a file with a sqlite URL (see README).");
        }

        var appEnv = r.Choice("APP_ENV", AtsSettings.Development, AppEnvs);
        var timezoneId = r.String("OFFICE_TIMEZONE", "Asia/Kolkata");
        var settings = new AtsSettings
        {
            AppEnv = appEnv,
            AppName = r.String("APP_NAME", "Office Attendance Tracker"),
            DatabaseUrl = databaseUrl,
            SecretKey = r.String("SECRET_KEY", ""),
            OfficeTimezone = timezoneId,
            TimeZone = FindIanaZone(timezoneId),
            SessionIdleMinutes = r.Int("SESSION_IDLE_MINUTES", 120),
            SessionAbsoluteHours = r.Int("SESSION_ABSOLUTE_HOURS", 12),
            SessionCookieName = r.String("SESSION_COOKIE_NAME", "ats_session"),
            CsrfCookieName = r.String("CSRF_COOKIE_NAME", "ats_csrf"),
            CsrfHeaderName = r.String("CSRF_HEADER_NAME", "X-CSRF-Token"),
            CookieSecure = r.OptionalBool("COOKIE_SECURE"),
            CookieSameSite = r.Choice("COOKIE_SAMESITE", "lax", SameSiteValues),
            CookieDomain = r.OptionalString("COOKIE_DOMAIN"),
            CorsOrigins = r.String("CORS_ORIGINS", "http://localhost:5173"),
            LoginMaxFailures = r.Int("LOGIN_MAX_FAILURES", 5),
            LoginWindowMinutes = r.Int("LOGIN_WINDOW_MINUTES", 15),
            LoginIpMaxFailures = r.Int("LOGIN_IP_MAX_FAILURES", 30),
            TrustProxyHeaders = r.Bool("TRUST_PROXY_HEADERS", false),
            PasswordLoginEnabled = r.Bool("PASSWORD_LOGIN_ENABLED", true),
            EntraTenantId = r.String("ENTRA_TENANT_ID", ""),
            EntraClientId = r.String("ENTRA_CLIENT_ID", ""),
            EntraClientSecret = r.String("ENTRA_CLIENT_SECRET", ""),
            EntraRedirectUri = r.String("ENTRA_REDIRECT_URI", ""),
            EntraAuthority = r.String("ENTRA_AUTHORITY", "https://login.microsoftonline.com"),
            DevLoginEnabled = r.Bool("DEV_LOGIN_ENABLED", false),
            MaxReportDays = r.Int("MAX_REPORT_DAYS", 366),
            ReportCompanyName = r.String("REPORT_COMPANY_NAME", ""),
            ReportLogoFile = r.String("REPORT_LOGO_FILE", ""),
            ReportBrandColor = r.String("REPORT_BRAND_COLOR", "#0F172A"),
            ReportAccentColor = r.String("REPORT_ACCENT_COLOR", "#2563EB"),
            LogLevel = r.String("LOG_LEVEL", "INFO"),
            WfoMappingFile = r.String("WFO_MAPPING_FILE", ""),
            EnvFile = envFile,
            BaseDirectory = baseDirectory,
        };
        return Validate(settings, warn);
    }

    private static AtsSettings Validate(AtsSettings s, Action<string>? warn)
    {
        if (s.IsProduction)
        {
            var lowered = s.SecretKey.ToLowerInvariant();
            if (s.SecretKey.Length < 32 || InsecureSecretMarkers.Any(lowered.Contains))
            {
                throw new SettingsException("SECRET_KEY must be a random value of at least 32 characters in production");
            }

            if (s.CookieSecure == false)
            {
                throw new SettingsException("COOKIE_SECURE cannot be disabled in production");
            }

            if (s.CorsOrigins.Contains('*', StringComparison.Ordinal))
            {
                throw new SettingsException("Wildcard CORS origins are not allowed in production");
            }
        }
        else if (s.SecretKey.Length == 0)
        {
            // Development convenience only: sessions will not survive a restart.
            warn?.Invoke("SECRET_KEY is not set; using an ephemeral key (sessions reset on restart)");
            s = Copy(s, secretKey: TokenUrlSafe(48), ephemeral: true);
        }

        if (s.CookieSameSite == "none" && !s.SecureCookies)
        {
            throw new SettingsException("COOKIE_SAMESITE=none requires secure cookies");
        }

        string[] entra = [s.EntraTenantId, s.EntraClientId, s.EntraClientSecret, s.EntraRedirectUri];
        if (entra.Any(v => v.Trim().Length > 0) && !entra.All(v => v.Trim().Length > 0))
        {
            throw new SettingsException(
                "Microsoft Entra ID sign-in needs all of ENTRA_TENANT_ID, ENTRA_CLIENT_ID, ENTRA_CLIENT_SECRET " +
                "and ENTRA_REDIRECT_URI (or none of them)");
        }

        if (s.IsProduction)
        {
            if (s.DevLoginEnabled)
            {
                throw new SettingsException("DEV_LOGIN_ENABLED must never be on in production");
            }

            if (s.EntraEnabled && !s.EntraRedirectUri.StartsWith("https://", StringComparison.Ordinal))
            {
                throw new SettingsException("ENTRA_REDIRECT_URI must use https in production");
            }
        }

        if (!(s.PasswordLoginEnabled || s.EntraEnabled || s.DevLoginEnabled))
        {
            throw new SettingsException(
                "No sign-in method is enabled: configure Microsoft Entra ID or set PASSWORD_LOGIN_ENABLED=true");
        }

        return s;
    }

    /// <summary>Python's <c>secrets.token_urlsafe(n)</c>: n random bytes, base64url without padding.</summary>
    public static string TokenUrlSafe(int bytes)
    {
        var data = RandomNumberGenerator.GetBytes(bytes);
        return Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    /// <summary>
    /// An IANA zone ("Asia/Kolkata"). Windows zone ids ("India Standard Time") are refused, as zoneinfo would.
    /// </summary>
    private static TimeZoneInfo FindIanaZone(string id)
    {
        if (id.Length > 0 && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var zone) && zone.HasIanaId)
        {
            return zone;
        }

        throw new SettingsException($"OFFICE_TIMEZONE '{id}' is not a valid IANA timezone");
    }

    private static AtsSettings Copy(AtsSettings s, string secretKey, bool ephemeral) => new()
    {
        AppEnv = s.AppEnv, AppName = s.AppName, DatabaseUrl = s.DatabaseUrl, SecretKey = secretKey,
        SecretKeyIsEphemeral = ephemeral, OfficeTimezone = s.OfficeTimezone, TimeZone = s.TimeZone,
        SessionIdleMinutes = s.SessionIdleMinutes, SessionAbsoluteHours = s.SessionAbsoluteHours,
        SessionCookieName = s.SessionCookieName, CsrfCookieName = s.CsrfCookieName, CsrfHeaderName = s.CsrfHeaderName,
        CookieSecure = s.CookieSecure, CookieSameSite = s.CookieSameSite, CookieDomain = s.CookieDomain,
        CorsOrigins = s.CorsOrigins, LoginMaxFailures = s.LoginMaxFailures, LoginWindowMinutes = s.LoginWindowMinutes,
        LoginIpMaxFailures = s.LoginIpMaxFailures, TrustProxyHeaders = s.TrustProxyHeaders,
        PasswordLoginEnabled = s.PasswordLoginEnabled, EntraTenantId = s.EntraTenantId,
        EntraClientId = s.EntraClientId, EntraClientSecret = s.EntraClientSecret,
        EntraRedirectUri = s.EntraRedirectUri, EntraAuthority = s.EntraAuthority, DevLoginEnabled = s.DevLoginEnabled,
        MaxReportDays = s.MaxReportDays, ReportCompanyName = s.ReportCompanyName, ReportLogoFile = s.ReportLogoFile,
        ReportBrandColor = s.ReportBrandColor, ReportAccentColor = s.ReportAccentColor, LogLevel = s.LogLevel,
        WfoMappingFile = s.WfoMappingFile, EnvFile = s.EnvFile, BaseDirectory = s.BaseDirectory,
    };

    /// <summary>Value parsing as pydantic does it for environment strings.</summary>
    private sealed class Reader(Func<string, string?> get)
    {
        private static readonly string[] TrueValues = ["1", "true", "t", "yes", "y", "on"];
        private static readonly string[] FalseValues = ["0", "false", "f", "no", "n", "off"];

        public string? Raw(string key) => get(key);

        /// <summary>A string field: an empty value is kept as an empty string (pydantic-settings does not ignore it).</summary>
        public string String(string key, string fallback) => get(key) ?? fallback;

        /// <summary>An optional string: empty counts as not set.</summary>
        public string? OptionalString(string key)
        {
            var v = get(key);
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        public int Int(string key, int fallback)
        {
            var v = get(key);
            if (v is null)
            {
                return fallback;
            }

            if (int.TryParse(v.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var n))
            {
                return n;
            }

            throw new SettingsException($"{key}: Input should be a valid integer, got '{v}'");
        }

        public bool Bool(string key, bool fallback) => OptionalBool(key) ?? fallback;

        public bool? OptionalBool(string key)
        {
            var v = get(key)?.Trim();
            if (string.IsNullOrEmpty(v))
            {
                return null;
            }

            var lower = v.ToLowerInvariant();
            if (TrueValues.Contains(lower))
            {
                return true;
            }

            if (FalseValues.Contains(lower))
            {
                return false;
            }

            throw new SettingsException($"{key}: Input should be a valid boolean, got '{v}'");
        }

        public string Choice(string key, string fallback, string[] allowed)
        {
            var v = get(key) ?? fallback;
            if (!allowed.Contains(v))
            {
                throw new SettingsException(
                    $"{key}: Input should be {string.Join(", ", allowed.Select(a => $"'{a}'"))}, got '{v}'");
            }

            return v;
        }
    }
}
