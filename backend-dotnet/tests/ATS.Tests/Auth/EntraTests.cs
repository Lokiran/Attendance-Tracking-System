using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using Ats.Infrastructure.Security;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace Ats.Tests.Auth;

/// <summary>
/// Port of test_auth_entra.py. No network: Microsoft's token endpoint and signing keys are replaced by a local RSA
/// key, so every check of the ID token (signature, issuer, audience, expiry, nonce, tenant) runs for real.
/// </summary>
public sealed class EntraTests : IDisposable
{
    private const string Tenant = "11111111-2222-3333-4444-555555555555";
    private const string ClientId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    private const string Oid = "0f0e0d0c-0b0a-0908-0706-050403020100";
    private static readonly RsaSecurityKey Key = new(RSA.Create(2048));
    private static readonly RsaSecurityKey OtherKey = new(RSA.Create(2048));

    private readonly FakeEntra _entra = new();
    private readonly AuthHost _host;

    public EntraTests() => _host = new AuthHost(EntraSettings, s => s.Replace(ServiceDescriptor.Singleton<IEntraHttp>(_entra)));

    public void Dispose() => _host.Dispose();

    private static Dictionary<string, string?> EntraSettings => new()
    {
        ["ENTRA_TENANT_ID"] = Tenant, ["ENTRA_CLIENT_ID"] = ClientId, ["ENTRA_CLIENT_SECRET"] = new string('x', 32),
        ["ENTRA_REDIRECT_URI"] = "http://localhost/api/auth/entra/callback",
    };

    private sealed class FakeEntra : IEntraHttp
    {
        public string Token { get; set; } = "";

        public Dictionary<string, string> Captured { get; } = [];

        public Task<JsonObject> PostTokenAsync(string tokenUrl, IReadOnlyDictionary<string, string> form, CancellationToken ct)
        {
            foreach (var (k, v) in form)
            {
                Captured[k] = v;
            }

            return Task.FromResult(new JsonObject { ["id_token"] = Token, ["token_type"] = "Bearer" });
        }

        public Task<IReadOnlyList<SecurityKey>> GetSigningKeysAsync(string jwksUri, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<SecurityKey>>([Key]);
    }

    private static string IdToken(string nonce, RsaSecurityKey? key = null, Dictionary<string, object?>? overrides = null)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var claims = new Dictionary<string, object?>
        {
            ["iss"] = $"https://login.microsoftonline.com/{Tenant}/v2.0", ["aud"] = ClientId, ["tid"] = Tenant, ["oid"] = Oid,
            ["sub"] = "pairwise-subject", ["nonce"] = nonce, ["iat"] = now, ["nbf"] = now, ["exp"] = now + 3600,
            ["preferred_username"] = "rahul.reddy@test.example", ["name"] = "Rahul Reddy",
        };
        foreach (var (k, v) in overrides ?? [])
        {
            claims[k] = v;
        }

        var payload = new JsonObject();
        foreach (var (k, v) in claims.Where(c => c.Value is not null))
        {
            payload[k] = JsonValue.Create(v);
        }

        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(payload.ToJsonString(), new SigningCredentials(key ?? Key, SecurityAlgorithms.RsaSha256));
    }

    private async Task<(HttpResponseMessage Response, string State, string Nonce)> Start(string? next = "/team")
    {
        var path = next is null ? "/api/auth/entra/login" : $"/api/auth/entra/login?next={Uri.EscapeDataString(next)}";
        var r = await _host.Client.GetAsync(new Uri(path, UriKind.Relative));
        r.StatusCode.Should().Be(HttpStatusCode.Found);
        var query = HttpUtility.ParseQueryString(r.Headers.Location!.Query);
        return (r, query["state"]!, query["nonce"]!);
    }

    private Task<HttpResponseMessage> Finish(string state, string token, string code = "auth-code")
    {
        _entra.Token = token;
        return _host.Client.GetAsync(new Uri($"/api/auth/entra/callback?code={code}&state={Uri.EscapeDataString(state)}", UriKind.Relative));
    }

    private async Task Refused(HttpResponseMessage r, string code)
    {
        r.StatusCode.Should().Be(HttpStatusCode.Found);
        r.Headers.Location!.OriginalString.Should().Be($"/login?error={code}");
        r.Headers.TryGetValues("Set-Cookie", out var cookies);
        (cookies ?? []).Should().NotContain(c => c.StartsWith("ats_session=", StringComparison.Ordinal));
        (await _host.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Sign_in_links_the_account_and_starts_a_session()
    {
        var (r, state, nonce) = await Start("/team?start=2026-03-01");
        var location = r.Headers.Location!.ToString();
        location.Should().StartWith($"https://login.microsoftonline.com/{Tenant}/oauth2/v2.0/authorize?");
        var q = HttpUtility.ParseQueryString(r.Headers.Location!.Query);
        q["client_id"].Should().Be(ClientId);
        q["code_challenge_method"].Should().Be("S256");
        q["response_type"].Should().Be("code");
        q["scope"].Should().Contain("openid");
        var flowCookie = r.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ats_oidc=", StringComparison.Ordinal));
        flowCookie.Should().ContainEquivalentOf("httponly").And.ContainEquivalentOf("path=/api/auth/entra").And.ContainEquivalentOf("samesite=lax");

        var done = await Finish(state, IdToken(nonce));
        done.StatusCode.Should().Be(HttpStatusCode.Found);
        done.Headers.Location!.OriginalString.Should().Be("/team?start=2026-03-01");
        _entra.Captured["code"].Should().Be("auth-code");
        _entra.Captured["code_verifier"].Length.Should().BeGreaterThanOrEqualTo(43); // PKCE verifier sent
        _entra.Captured["grant_type"].Should().Be("authorization_code");
        // the challenge Microsoft got is the S256 of the verifier we sent
        Base64UrlEncoder.Encode(SHA256.HashData(System.Text.Encoding.ASCII.GetBytes(_entra.Captured["code_verifier"]))).Should().Be(q["code_challenge"]);

        var me = await _host.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative));
        me.StatusCode.Should().Be(HttpStatusCode.OK);
        JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement.GetProperty("user").GetProperty("email").GetString()
            .Should().Be("rahul.reddy@test.example");
        await using var db = _host.Db();
        (await db.Users.SingleAsync(u => u.Id == _host.Org.Rahul.Id)).GraphUserId.Should().Be(Oid);
        var actions = await db.AuditLogs.Select(a => new { a.Action, a.AfterValues }).ToListAsync();
        actions.Should().Contain(a => a.Action == "USER_IDENTITY_LINKED");
        actions.Should().Contain(a => a.Action == "LOGIN" && a.AfterValues!["method"]!.GetValue<string>() == "entra");
    }

    [Fact]
    public async Task A_second_sign_in_matches_by_object_id_even_if_the_email_changed()
    {
        await using (var db = _host.Db())
        {
            (await db.Users.SingleAsync(u => u.Id == _host.Org.Rahul.Id)).GraphUserId = Oid;
            await db.SaveChangesAsync();
        }

        var (_, state, nonce) = await Start();
        var r = await Finish(state, IdToken(nonce, overrides: new() { ["preferred_username"] = "renamed@test.example" }));
        r.Headers.Location!.OriginalString.Should().Be("/team");
        var me = await _host.Client.GetAsync(new Uri("/api/auth/me", UriKind.Relative));
        JsonDocument.Parse(await me.Content.ReadAsStringAsync()).RootElement.GetProperty("user").GetProperty("full_name").GetString().Should().Be("Rahul Reddy");
    }

    public static TheoryData<string, object?> BadClaims()
    {
        var past = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200;
        return new()
        {
            { "aud", "someone-else" },
            { "iss", "https://login.microsoftonline.com/other-tenant/v2.0" },
            { "tid", "other-tenant" },
            { "exp", past + 3600 - 3600 },
            { "oid", null },
            { "nonce", "replayed-nonce" },
            { "sub", null },
        };
    }

    [Theory]
    [MemberData(nameof(BadClaims))]
    public async Task Bad_id_tokens_are_refused(string claim, object? value)
    {
        var (_, state, nonce) = await Start();
        var overrides = new Dictionary<string, object?> { [claim] = value };
        if (claim == "exp")
        {
            overrides["iat"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200;
            overrides["nbf"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 7200;
        }

        await Refused(await Finish(state, IdToken(nonce, overrides: overrides)), "invalid_token");
    }

    [Fact]
    public async Task A_token_signed_by_another_key_is_refused()
    {
        var (_, state, nonce) = await Start();
        await Refused(await Finish(state, IdToken(nonce, OtherKey)), "invalid_token");
    }

    [Fact]
    public async Task The_state_must_match_the_flow_cookie()
    {
        var (_, _, nonce) = await Start();
        await Refused(await Finish("forged-state", IdToken(nonce)), "sign_in_expired");
    }

    [Fact]
    public async Task A_missing_or_tampered_flow_cookie_is_refused()
    {
        var (start, state, nonce) = await Start();
        var flow = start.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("ats_oidc=", StringComparison.Ordinal)).Split(';')[0]["ats_oidc=".Length..];
        using var fresh = _host.Api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
        _entra.Token = IdToken(nonce);

        using var tampered = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/entra/callback?code=c&state={Uri.EscapeDataString(state)}");
        tampered.Headers.Add("Cookie", $"ats_oidc={flow[..^3]}AAA");
        var r1 = await fresh.SendAsync(tampered);
        r1.Headers.Location!.OriginalString.Should().Be("/login?error=sign_in_expired");

        using var missing = new HttpRequestMessage(HttpMethod.Get, $"/api/auth/entra/callback?code=c&state={Uri.EscapeDataString(state)}");
        var r2 = await fresh.SendAsync(missing);
        r2.Headers.Location!.OriginalString.Should().Be("/login?error=sign_in_expired");
    }

    [Fact]
    public async Task Unknown_inactive_and_mismatched_accounts_are_refused()
    {
        var (_, state, nonce) = await Start();
        await Refused(await Finish(state, IdToken(nonce, overrides: new() { ["preferred_username"] = "stranger@test.example" })), "no_account");

        await using (var db = _host.Db())
        {
            (await db.Users.SingleAsync(u => u.Id == _host.Org.Sneha.Id)).IsActive = false;
            (await db.Users.SingleAsync(u => u.Id == _host.Org.Arjun.Id)).GraphUserId = "12345678-0000-0000-0000-000000000000";
            await db.SaveChangesAsync();
        }

        (_, state, nonce) = await Start();
        await Refused(await Finish(state, IdToken(nonce, overrides: new()
        {
            ["preferred_username"] = "sneha.rao@test.example", ["oid"] = "99999999-0000-0000-0000-000000000000",
        })), "account_inactive");

        (_, state, nonce) = await Start();
        await Refused(await Finish(state, IdToken(nonce, overrides: new()
        {
            ["preferred_username"] = "arjun.kumar@test.example", ["oid"] = "87654321-0000-0000-0000-000000000000",
        })), "account_mismatch");
        await using var check = _host.Db();
        (await check.Users.SingleAsync(u => u.Id == _host.Org.Arjun.Id)).GraphUserId.Should().Be("12345678-0000-0000-0000-000000000000");
        (await check.AuditLogs.CountAsync(a => a.Action == "USER_IDENTITY_LINKED")).Should().Be(0); // refused attempts save nothing
    }

    [Fact]
    public async Task Cancelling_at_microsoft()
    {
        await Start();
        var r = await _host.Client.GetAsync(new Uri("/api/auth/entra/callback?error=access_denied&error_description=cancelled", UriKind.Relative));
        await Refused(r, "entra_denied");
    }

    [Theory]
    [InlineData("https://evil.example/")]
    [InlineData("//evil.example")]
    [InlineData("/\\evil.example")]
    [InlineData("javascript:x")]
    public async Task Next_cannot_redirect_off_site(string next)
    {
        var (_, state, nonce) = await Start(next);
        var r = await Finish(state, IdToken(nonce));
        r.Headers.Location!.OriginalString.Should().Be("/");
    }

    [Fact]
    public async Task Providers_reflect_the_configuration() =>
        JsonDocument.Parse(await _host.Client.GetStringAsync(new Uri("/api/auth/providers", UriKind.Relative))).RootElement.GetRawText()
            .Should().Be("""{"password":true,"entra":true,"dev":false}""");
}

/// <summary>Entra off, dev login and the role policies.</summary>
public sealed class SignInSwitchTests
{
    private static async Task<JsonElement> Json(HttpResponseMessage r) => JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Entra_endpoints_are_404_when_not_configured()
    {
        using var host = new AuthHost();
        (await host.Client.GetAsync(new Uri("/api/auth/entra/login", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await host.Client.GetAsync(new Uri("/api/auth/entra/callback", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        JsonDocument.Parse(await host.Client.GetStringAsync(new Uri("/api/auth/providers", UriKind.Relative))).RootElement.GetRawText()
            .Should().Be("""{"password":true,"entra":false,"dev":false}""");
    }

    [Fact]
    public async Task Dev_login_is_off_by_default()
    {
        using var host = new AuthHost();
        (await host.Client.PostAsJsonAsync("/api/auth/dev-login", new { email = host.Org.Rahul.Email })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Dev_login_when_enabled()
    {
        using var host = new AuthHost(new Dictionary<string, string?> { ["DEV_LOGIN_ENABLED"] = "true" });
        var r = await host.Client.PostAsJsonAsync("/api/auth/dev-login", new { email = host.Org.Priya.Email });
        r.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(r)).GetProperty("user").GetProperty("role").GetString().Should().Be("MANAGER");
        (await host.Client.GetAsync(new Uri("/api/_policy/team", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.OK); // a real session with the user's own role
        await using (var db = host.Db())
        {
            (await db.AuditLogs.SingleAsync(a => a.Action == "LOGIN")).AfterValues!.ToJsonString().Should().Be("""{"method":"dev"}""");
            (await db.Users.SingleAsync(u => u.Id == host.Org.Sneha.Id)).IsActive = false;
            (await db.Users.SingleAsync(u => u.Id == host.Org.Rahul.Id)).EmployeeCode = "Intern - 10001";
            await db.SaveChangesAsync();
        }

        (await host.Client.PostAsJsonAsync("/api/auth/dev-login", new { email = host.Org.Sneha.Email })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await host.Client.PostAsJsonAsync("/api/auth/dev-login", new { email = "nobody@test.example" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        // an employee ID works too (people added from a timesheet have placeholder emails)
        var byCode = await host.Client.PostAsJsonAsync("/api/auth/dev-login", new { email = "  intern  -  10001 " });
        byCode.StatusCode.Should().Be(HttpStatusCode.OK);
        (await Json(byCode)).GetProperty("user").GetProperty("full_name").GetString().Should().Be("Rahul Reddy");
        (await host.Client.PostAsJsonAsync("/api/auth/dev-login", new { email = "NOPE-1" })).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Role_policies_follow_the_python_dependencies()
    {
        using var host = new AuthHost();
        Task<HttpResponseMessage> Get(string p) => host.Client.GetAsync(new Uri(p, UriKind.Relative));

        var anonymous = await Get("/api/_policy/team");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await Json(anonymous)).GetProperty("error").GetProperty("code").GetString().Should().Be("not_authenticated");

        var csrf = await host.LoginAsync(host.Org.Rahul); // employee
        (await Get("/api/_policy/user")).StatusCode.Should().Be(HttpStatusCode.OK);
        var denied = await Get("/api/_policy/team");
        denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await Json(denied)).GetProperty("error").GetProperty("message").GetString().Should().Be("You do not have permission to perform this action");
        (await Get("/api/_policy/admin")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        // CSRF is checked before the role (Python: get_auth runs before require_roles)
        var noCsrf = await host.PostAsync("/api/_policy/admin");
        (await Json(noCsrf)).GetProperty("error").GetProperty("code").GetString().Should().Be("csrf_failed");
        (await Json(await host.PostAsync("/api/_policy/admin", csrf: csrf))).GetProperty("error").GetProperty("code").GetString().Should().Be("forbidden");

        await host.LoginAsync(host.Org.Priya); // manager
        (await Get("/api/_policy/team")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await Get("/api/_policy/admin")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var adminCsrf = await host.LoginAsync(host.Org.Admin);
        (await Get("/api/_policy/team")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.PostAsync("/api/_policy/admin", csrf: adminCsrf)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Public_endpoints_ignore_a_stale_session_and_never_check_csrf()
    {
        using var host = new AuthHost();
        await host.LoginAsync(host.Org.Rahul);
        host.Clock.UtcNow += TimeSpan.FromDays(2); // the session cookie is now expired
        (await host.Client.GetAsync(new Uri("/api/health", UriKind.Relative))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await host.Client.PostAsJsonAsync("/api/auth/login", new { email = host.Org.Rahul.Email, password = AuthHost.Password }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
