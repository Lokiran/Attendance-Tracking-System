using Ats.Application.Configuration;
using FluentAssertions;

namespace Ats.Tests.Unit;

/// <summary>The startup rules of backend/app/core/config.py (tests in test_database_selection.py / test_auth_entra.py).</summary>
public sealed class SettingsLoaderTests
{
    private const string GoodSecret = "a-random-production-value-0123456789-xyz";

    private static AtsSettings Load(params (string Key, string Value)[] values)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DATABASE_URL"] = "postgresql+psycopg://ats:pw@localhost:5432/ats",
        };
        foreach (var (key, value) in values)
        {
            map[key] = value;
        }

        return SettingsLoader.Load(k => map.TryGetValue(k, out var v) ? v : null, "/base");
    }

    [Fact]
    public void Defaults_match_the_python_settings()
    {
        var s = Load(("SECRET_KEY", "dev-key"));
        s.AppEnv.Should().Be("development");
        s.OfficeTimezone.Should().Be("Asia/Kolkata");
        s.TimeZone.Id.Should().Be("Asia/Kolkata");
        s.SessionIdleMinutes.Should().Be(120);
        s.SessionAbsoluteHours.Should().Be(12);
        s.SessionCookieName.Should().Be("ats_session");
        s.CsrfCookieName.Should().Be("ats_csrf");
        s.CsrfHeaderName.Should().Be("X-CSRF-Token");
        s.CookieSameSite.Should().Be("lax");
        s.SecureCookies.Should().BeFalse();
        s.CorsOriginList.Should().Equal("http://localhost:5173");
        s.LoginMaxFailures.Should().Be(5);
        s.LoginWindowMinutes.Should().Be(15);
        s.LoginIpMaxFailures.Should().Be(30);
        s.PasswordLoginEnabled.Should().BeTrue();
        s.EntraEnabled.Should().BeFalse();
        s.DevLoginEnabled.Should().BeFalse();
        s.MaxReportDays.Should().Be(366);
        s.ReportBrandColor.Should().Be("#0F172A");
    }

    [Fact]
    public void Database_url_is_required()
    {
        var act = () => SettingsLoader.Load(_ => null, "/base");
        act.Should().Throw<SettingsException>().WithMessage("DATABASE_URL is not set*");
        var blank = () => SettingsLoader.Load(k => k == "DATABASE_URL" ? "   " : null, "/base");
        blank.Should().Throw<SettingsException>();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData("on", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("Off", false)]
    [InlineData("0", false)]
    public void Booleans_are_read_like_pydantic(string raw, bool expected) =>
        Load(("SECRET_KEY", "k"), ("TRUST_PROXY_HEADERS", raw)).TrustProxyHeaders.Should().Be(expected);

    [Fact]
    public void Bad_values_are_refused()
    {
        ((Action)(() => Load(("SESSION_IDLE_MINUTES", "soon")))).Should().Throw<SettingsException>().WithMessage("*integer*");
        ((Action)(() => Load(("DEV_LOGIN_ENABLED", "maybe")))).Should().Throw<SettingsException>().WithMessage("*boolean*");
        ((Action)(() => Load(("APP_ENV", "staging")))).Should().Throw<SettingsException>();
        ((Action)(() => Load(("COOKIE_SAMESITE", "Lax")))).Should().Throw<SettingsException>();
    }

    [Fact]
    public void Timezone_must_be_an_iana_zone()
    {
        ((Action)(() => Load(("OFFICE_TIMEZONE", "Mars/Olympus")))).Should().Throw<SettingsException>()
            .WithMessage("OFFICE_TIMEZONE 'Mars/Olympus' is not a valid IANA timezone");
        ((Action)(() => Load(("OFFICE_TIMEZONE", "India Standard Time")))).Should().Throw<SettingsException>();
        Load(("SECRET_KEY", "k"), ("OFFICE_TIMEZONE", "Europe/London")).TimeZone.Id.Should().Be("Europe/London");
    }

    [Fact]
    public void Missing_secret_outside_production_becomes_an_ephemeral_key_with_a_warning()
    {
        var warnings = new List<string>();
        var s = SettingsLoader.Load(k => k == "DATABASE_URL" ? "sqlite://" : null, "/base", null, warnings.Add);
        s.SecretKeyIsEphemeral.Should().BeTrue();
        s.SecretKey.Should().HaveLength(64); // token_urlsafe(48)
        warnings.Should().ContainSingle().Which.Should().Contain("ephemeral");
    }

    [Theory]
    [InlineData("short")]
    [InlineData("this-is-long-enough-but-says-change_me-0000")]
    [InlineData("an-example-value-that-is-long-enough-12345")]
    public void Production_refuses_weak_secrets(string secret) =>
        ((Action)(() => Load(("APP_ENV", "production"), ("SECRET_KEY", secret)))).Should().Throw<SettingsException>()
            .WithMessage("SECRET_KEY must be a random value*");

    [Fact]
    public void Production_guards()
    {
        (string, string)[] prod = [("APP_ENV", "production"), ("SECRET_KEY", GoodSecret)];
        Load(prod).SecureCookies.Should().BeTrue();
        ((Action)(() => Load([.. prod, ("COOKIE_SECURE", "false")]))).Should().Throw<SettingsException>()
            .WithMessage("COOKIE_SECURE cannot be disabled in production");
        ((Action)(() => Load([.. prod, ("CORS_ORIGINS", "*")]))).Should().Throw<SettingsException>()
            .WithMessage("Wildcard CORS origins*");
        ((Action)(() => Load([.. prod, ("DEV_LOGIN_ENABLED", "true")]))).Should().Throw<SettingsException>()
            .WithMessage("DEV_LOGIN_ENABLED must never be on in production");
        ((Action)(() => Load([.. prod, ("ENTRA_TENANT_ID", "t"), ("ENTRA_CLIENT_ID", "c"), ("ENTRA_CLIENT_SECRET", "s"),
                              ("ENTRA_REDIRECT_URI", "http://app/api/auth/entra/callback")])))
            .Should().Throw<SettingsException>().WithMessage("ENTRA_REDIRECT_URI must use https in production");
    }

    [Fact]
    public void Entra_needs_all_four_values_or_none()
    {
        ((Action)(() => Load(("SECRET_KEY", "k"), ("ENTRA_TENANT_ID", "t")))).Should().Throw<SettingsException>()
            .WithMessage("Microsoft Entra ID sign-in needs all of*");
        Load(("SECRET_KEY", "k"), ("ENTRA_TENANT_ID", "t"), ("ENTRA_CLIENT_ID", "c"), ("ENTRA_CLIENT_SECRET", "s"),
             ("ENTRA_REDIRECT_URI", "http://localhost:5173/api/auth/entra/callback")).EntraEnabled.Should().BeTrue();
    }

    [Fact]
    public void Samesite_none_needs_secure_cookies()
    {
        ((Action)(() => Load(("SECRET_KEY", "k"), ("COOKIE_SAMESITE", "none")))).Should().Throw<SettingsException>()
            .WithMessage("COOKIE_SAMESITE=none requires secure cookies");
        Load(("SECRET_KEY", "k"), ("COOKIE_SAMESITE", "none"), ("COOKIE_SECURE", "true")).SecureCookies.Should().BeTrue();
    }

    [Fact]
    public void At_least_one_sign_in_method()
    {
        ((Action)(() => Load(("SECRET_KEY", "k"), ("PASSWORD_LOGIN_ENABLED", "false")))).Should().Throw<SettingsException>()
            .WithMessage("No sign-in method is enabled*");
        Load(("SECRET_KEY", "k"), ("PASSWORD_LOGIN_ENABLED", "false"), ("DEV_LOGIN_ENABLED", "true"))
            .DevLoginEnabled.Should().BeTrue();
    }

    [Fact]
    public void Empty_strings_are_kept_and_an_empty_optional_is_unset()
    {
        var s = Load(("SECRET_KEY", "k"), ("REPORT_BRAND_COLOR", ""), ("COOKIE_DOMAIN", ""), ("COOKIE_SECURE", ""));
        s.ReportBrandColor.Should().BeEmpty(); // as pydantic-settings: the export falls back to its default colour
        s.CookieDomain.Should().BeNull();
        s.CookieSecure.Should().BeNull();
    }
}
