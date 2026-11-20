using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace Ats.Tests.Auth;

/// <summary>Port of backend/app/tests/test_auth.py, plus request validation and the /me shape.</summary>
public sealed class AuthApiTests : IDisposable
{
    private readonly AuthHost _host = new();

    public void Dispose() => _host.Dispose();

    private static async Task<JsonElement> Json(HttpResponseMessage r) =>
        JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    private Task<HttpResponseMessage> Login(string email, string password) =>
        _host.Client.PostAsJsonAsync("/api/auth/login", new { email, password });

    private Task<HttpResponseMessage> Me() => _host.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative));

    [Fact]
    public async Task Login_success_sets_httponly_cookie_and_hides_secrets()
    {
        var r = await Login(_host.Org.Rahul.Email, AuthHost.Password);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        var text = await r.Content.ReadAsStringAsync();
        var body = JsonDocument.Parse(text).RootElement;
        body.GetProperty("user").GetProperty("full_name").GetString().Should().Be("Rahul Reddy");
        body.GetProperty("user").GetProperty("manager").GetProperty("full_name").GetString().Should().Be("Priya Sharma");
        text.Should().NotContain("password_hash").And.NotContain(AuthHost.Password);
        var cookies = r.Headers.GetValues("Set-Cookie").ToList();
        var session = cookies.Single(c => c.StartsWith("ats_session=", StringComparison.Ordinal));
        session.Should().ContainEquivalentOf("httponly").And.ContainEquivalentOf("samesite=lax").And.ContainEquivalentOf("path=/")
            .And.ContainEquivalentOf("max-age=43200");
        cookies.Single(c => c.StartsWith("ats_csrf=", StringComparison.Ordinal)).Should().NotContainEquivalentOf("httponly");
    }

    [Fact]
    public async Task The_python_argon2_hash_verifies_and_the_session_works()
    {
        (await Login(_host.Org.Admin.Email, AuthHost.Password)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Me()).StatusCode.Should().Be(HttpStatusCode.OK);
        await using var db = _host.Db();
        (await db.Users.SingleAsync(u => u.Id == _host.Org.Admin.Id)).PasswordHash.Should().Be(AuthHost.PythonHash); // no rehash needed
    }

    [Fact]
    public async Task Me_has_the_python_shape()
    {
        await _host.LoginAsync(_host.Org.Priya);
        var body = await Json(await Me());
        body.EnumerateObject().Select(p => p.Name).Should().Equal("user", "csrf_token", "office_timezone", "has_reports", "latest_timesheet_date");
        body.GetProperty("user").EnumerateObject().Select(p => p.Name).Should().Equal(
            "id", "employee_code", "full_name", "email", "role", "job_title", "department", "manager", "date_joined",
            "is_active", "attendance_tracked");
        body.GetProperty("user").GetProperty("role").GetString().Should().Be("MANAGER");
        body.GetProperty("user").GetProperty("department").GetRawText().Should().Be($$"""{"id":{{_host.Org.Ai.Id}},"name":"AI Engineering"}""");
        body.GetProperty("user").GetProperty("date_joined").GetString().Should().Be("2025-01-01");
        body.GetProperty("office_timezone").GetString().Should().Be("Asia/Kolkata");
        body.GetProperty("has_reports").GetBoolean().Should().BeTrue();
        body.GetProperty("latest_timesheet_date").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task Login_is_case_insensitive_on_email() =>
        (await Login(_host.Org.Rahul.Email.ToUpperInvariant(), AuthHost.Password)).StatusCode.Should().Be(HttpStatusCode.OK);

    [Fact]
    public async Task Login_failure_is_generic()
    {
        var wrong = await Login(_host.Org.Rahul.Email, "nope-nope-1");
        var missing = await Login("ghost@test.example", "nope-nope-1");
        wrong.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Json(wrong)).GetProperty("error").GetProperty("message").GetString().Should().Be("Invalid email or password");
        (await Json(missing)).GetProperty("error").GetProperty("code").GetString().Should().Be("invalid_credentials");
    }

    [Fact]
    public async Task Inactive_user_cannot_login()
    {
        await using (var db = _host.Db())
        {
            (await db.Users.SingleAsync(u => u.Id == _host.Org.Sneha.Id)).IsActive = false;
            await db.SaveChangesAsync();
        }

        (await Login(_host.Org.Sneha.Email, AuthHost.Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_is_rate_limited_after_repeated_failures_and_the_window_expires()
    {
        for (var i = 0; i < 5; i++)
        {
            await Login(_host.Org.Rahul.Email, "bad-password-1");
        }

        var r = await Login(_host.Org.Rahul.Email, AuthHost.Password);
        ((int)r.StatusCode).Should().Be(429);
        r.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromMinutes(15));
        (await Json(r)).GetProperty("error").GetProperty("message").GetString()
            .Should().Be("Too many failed sign-in attempts. Try again in 15 minutes.");

        _host.Clock.UtcNow += TimeSpan.FromMinutes(16);
        (await Login(_host.Org.Rahul.Email, AuthHost.Password)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Me_requires_authentication()
    {
        var r = await Me();
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Json(r)).GetProperty("error").GetProperty("code").GetString().Should().Be("not_authenticated");
    }

    [Fact]
    public async Task Logout_revokes_the_session()
    {
        var csrf = await _host.LoginAsync(_host.Org.Rahul);
        (await Me()).StatusCode.Should().Be(HttpStatusCode.OK);
        var logout = await _host.PostAsync("/api/auth/logout", csrf: csrf);
        logout.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(logout)).GetProperty("message").GetString().Should().Be("Signed out");
        logout.Headers.GetValues("Set-Cookie").Should().Contain(c => c.StartsWith("ats_session=;", StringComparison.Ordinal));

        await using var db = _host.Db();
        var session = await db.UserSessions.SingleAsync(s => s.UserId == _host.Org.Rahul.Id);
        session.RevokedAt.Should().NotBeNull();
        (await db.AuditLogs.CountAsync(a => a.Action == "LOGOUT")).Should().Be(1);
    }

    [Fact]
    public async Task A_replayed_cookie_after_logout_is_refused()
    {
        var r = await Login(_host.Org.Rahul.Email, AuthHost.Password);
        var cookie = r.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ats_session=", StringComparison.Ordinal)).Split(';')[0];
        var csrf = (await Json(r)).GetProperty("csrf_token").GetString();
        await _host.PostAsync("/api/auth/logout", csrf: csrf);

        using var replay = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        replay.Headers.Add("Cookie", cookie);
        using var fresh = _host.Api.CreateClient();
        var refused = await fresh.SendAsync(replay);
        refused.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Json(refused)).GetProperty("error").GetProperty("code").GetString().Should().Be("session_invalid");
    }

    [Fact]
    public async Task State_changing_requests_require_the_csrf_token()
    {
        await _host.LoginAsync(_host.Org.Rahul);
        var none = await _host.PostAsync("/api/auth/logout");
        none.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(none)).GetProperty("error").GetProperty("code").GetString().Should().Be("csrf_failed");
        (await _host.PostAsync("/api/auth/logout", csrf: "forged")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Me()).StatusCode.Should().Be(HttpStatusCode.OK); // still signed in
    }

    [Fact]
    public async Task Sessions_expire_when_idle()
    {
        await _host.LoginAsync(_host.Org.Rahul);
        _host.Clock.UtcNow += TimeSpan.FromMinutes(121);
        var r = await Me();
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Json(r)).GetProperty("error").GetProperty("code").GetString().Should().Be("session_expired");
    }

    [Fact]
    public async Task Sessions_expire_after_the_absolute_lifetime()
    {
        await _host.LoginAsync(_host.Org.Rahul);
        for (var i = 0; i < 13; i++) // keep it active, but exceed the 12 h absolute lifetime
        {
            _host.Clock.UtcNow += TimeSpan.FromMinutes(60);
            await Me();
        }

        (await Me()).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task The_session_token_is_stored_only_as_a_keyed_hash()
    {
        var r = await Login(_host.Org.Rahul.Email, AuthHost.Password);
        var token = r.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ats_session=", StringComparison.Ordinal)).Split(';')[0]["ats_session=".Length..];
        await using var db = _host.Db();
        var stored = (await db.UserSessions.SingleAsync()).TokenHash;
        stored.Should().NotBe(token).And.HaveLength(64).And.MatchRegex("^[0-9a-f]{64}$");
        stored.Should().Be(Ats.Application.Auth.Tokens.Hash(token, "test-only-key-0123456789-abcdefghijklmnopqrstuvwxyz"));
    }

    [Fact]
    public async Task Change_password_is_audited_without_secrets_and_signs_out_other_sessions()
    {
        var csrf = await _host.LoginAsync(_host.Org.Rahul);
        using (var other = _host.Api.CreateClient())
        {
            (await other.PostAsJsonAsync("/api/auth/login", new { email = _host.Org.Rahul.Email, password = AuthHost.Password })).EnsureSuccessStatusCode();
        }

        var r = await _host.PostAsync("/api/auth/change-password", new { current_password = AuthHost.Password, new_password = "An0therStrongOne" }, csrf);
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(r)).GetProperty("message").GetString().Should().Be("Password changed. Other sessions have been signed out.");
        (await Me()).StatusCode.Should().Be(HttpStatusCode.OK); // this session stays

        await using (var db = _host.Db())
        {
            (await db.UserSessions.CountAsync(s => s.UserId == _host.Org.Rahul.Id && s.RevokedAt == null)).Should().Be(1);
            var log = await db.AuditLogs.SingleAsync(a => a.Action == "PASSWORD_CHANGED");
            ((log.AfterValues?.ToJsonString() ?? "") + (log.BeforeValues?.ToJsonString() ?? "")).Should().NotContain("An0ther");
            (await db.Users.SingleAsync(u => u.Id == _host.Org.Rahul.Id)).PasswordHash.Should().StartWith("$argon2id$v=19$m=65536,t=3,p=4$");
        }

        await _host.PostAsync("/api/auth/logout", csrf: csrf);
        (await Login(_host.Org.Rahul.Email, AuthHost.Password)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Login(_host.Org.Rahul.Email, "An0therStrongOne")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("short", "Password must be at least 10 characters long")]
    [InlineData("onlyletters-here", "Password must contain both letters and digits")]
    public async Task Weak_passwords_are_refused(string password, string message)
    {
        var csrf = await _host.LoginAsync(_host.Org.Rahul);
        var r = await _host.PostAsync("/api/auth/change-password", new { current_password = AuthHost.Password, new_password = password }, csrf);
        ((int)r.StatusCode).Should().Be(422);
        (await Json(r)).GetProperty("error").GetProperty("message").GetString().Should().Be(message);
    }

    [Fact]
    public async Task A_wrong_current_password_is_401()
    {
        var csrf = await _host.LoginAsync(_host.Org.Rahul);
        var r = await _host.PostAsync("/api/auth/change-password", new { current_password = "wrong-one-1", new_password = "An0therStrongOne" }, csrf);
        r.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Json(r)).GetProperty("error").GetProperty("message").GetString().Should().Be("Current password is incorrect");
    }

    [Theory]
    [InlineData("""{"email": "not-an-email", "password": "x"}""", "email", "value is not a valid email address: An email address must have an @-sign.")]
    [InlineData("""{"email": "a@b", "password": "x"}""", "email", "value is not a valid email address: The part after the @-sign is not valid. It should have a period.")]
    [InlineData("""{"email": "a@test.test", "password": "x"}""", "email", "value is not a valid email address: The part after the @-sign is a special-use or reserved name that cannot be used with email.")]
    [InlineData("""{"email": "rahul.reddy@test.example"}""", "password", "Field required")]
    [InlineData("""{"email": "rahul.reddy@test.example", "password": ""}""", "password", "String should have at least 1 character")]
    [InlineData("""{"email": 5, "password": "x"}""", "email", "Input should be a valid string")]
    public async Task Login_bodies_are_validated_like_pydantic(string json, string field, string message)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var r = await _host.Client.PostAsync(new Uri("/api/auth/login", UriKind.Relative), content);
        ((int)r.StatusCode).Should().Be(422);
        var error = (await Json(r)).GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("validation_error");
        error.GetProperty("details")[0].GetProperty("field").GetString().Should().Be(field);
        error.GetProperty("details")[0].GetProperty("message").GetString().Should().Be(message);
        error.GetProperty("message").GetString().Should().Be($"{field}: {message}");
    }

    [Fact]
    public async Task A_missing_body_is_422()
    {
        using var content = new StringContent("", System.Text.Encoding.UTF8, "application/json");
        var r = await _host.Client.PostAsync(new Uri("/api/auth/login", UriKind.Relative), content);
        ((int)r.StatusCode).Should().Be(422);
        var error = (await Json(r)).GetProperty("error");
        error.GetProperty("message").GetString().Should().Be("Invalid request");
        error.GetProperty("details")[0].GetProperty("message").GetString().Should().Be("Field required");
    }

    [Fact]
    public async Task Login_is_audited_with_the_method()
    {
        await _host.LoginAsync(_host.Org.Rahul);
        await using var db = _host.Db();
        var log = await db.AuditLogs.SingleAsync(a => a.Action == "LOGIN");
        log.AfterValues!.ToJsonString().Should().Be("""{"method":"password"}""");
        log.EntityId.Should().Be(_host.Org.Rahul.Id.ToString(System.Globalization.CultureInfo.InvariantCulture));
        (await db.LoginAttempts.SingleAsync()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task Password_login_can_be_turned_off()
    {
        using var host = new AuthHost(new Dictionary<string, string?> { ["PASSWORD_LOGIN_ENABLED"] = "false", ["DEV_LOGIN_ENABLED"] = "true" });
        var r = await host.Client.PostAsJsonAsync("/api/auth/login", new { email = host.Org.Rahul.Email, password = AuthHost.Password });
        r.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(r)).GetProperty("error").GetProperty("code").GetString().Should().Be("password_login_disabled");
    }
}
