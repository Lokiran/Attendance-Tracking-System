using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text.Json;
using Ats.Application.Common;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Ats.Tests.Api;

/// <summary>Raises each error the API translates (test assembly only).</summary>
[ApiController]
[Route("api/_test")]
public sealed class TestErrorsController : ControllerBase
{
    [HttpGet("unauthorized")]
    public IActionResult Unauthorized_() => throw new UnauthorizedException("Your session has expired. Please sign in again.", "session_expired");

    [HttpGet("throttled")]
    public IActionResult Throttled() => throw new TooManyRequestsException("Too many failed sign-in attempts.", 900);

    [HttpGet("invalid")]
    public IActionResult Invalid() => throw new ValidationFailedException("End date must be on or after start date");

    [HttpGet("constraint")]
    public IActionResult Constraint() => throw new SqliteException("UNIQUE constraint failed: users.email", 19);

    [HttpGet("busy")]
    public IActionResult Busy() => throw new SqliteException("database is locked", 5);

    [HttpGet("boom")]
    public IActionResult Boom() => throw new InvalidOperationException("secret detail that must not leak");

    [HttpGet("paged")]
    public IActionResult Paged([FromQuery, Range(1, int.MaxValue)] int page = 1) => Ok(new { page });
}

public sealed class PipelineTests : IClassFixture<PipelineTests.Factory>
{
    private readonly Factory _factory;

    public PipelineTests(Factory factory) => _factory = factory;

    public sealed class Factory : IDisposable
    {
        public ApiFactory Api { get; } = new();

        public HttpClient Client { get; }

        public Factory()
        {
            Client = Api.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
                s.AddControllers().AddApplicationPart(typeof(TestErrorsController).Assembly))).CreateClient();
        }

        public void Dispose()
        {
            Client.Dispose();
            Api.Dispose();
        }
    }

    private static async Task<JsonElement> Json(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

    [Fact]
    public async Task Health_answers_ok_with_the_server_time()
    {
        var response = await _factory.Client.GetAsync(new Uri("/api/health", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await Json(response);
        body.GetProperty("status").GetString().Should().Be("ok");
        body.GetProperty("time").GetString().Should().MatchRegex(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d(\.\d{6})?Z$");
    }

    [Fact]
    public async Task Health_db_reports_the_dialect()
    {
        var body = await Json(await _factory.Client.GetAsync(new Uri("/api/health/db", UriKind.Relative)));
        body.GetProperty("status").GetString().Should().Be("ok");
        body.GetProperty("database").GetString().Should().Be("sqlite");
    }

    [Fact]
    public async Task Health_db_is_503_without_the_envelope_when_postgres_is_unreachable()
    {
        using var api = new ApiFactory(new Dictionary<string, string?>
        {
            ["DATABASE_URL"] = "postgresql+psycopg://ats:pw@127.0.0.1:1/ats?connect_timeout=2",
        });
        using var client = api.CreateClient();
        var response = await client.GetAsync(new Uri("/api/health/db", UriKind.Relative));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await response.Content.ReadAsStringAsync()).Should().Be("""{"status":"unavailable","database":"unreachable"}""");
    }

    [Fact]
    public async Task Every_response_carries_the_request_id_and_security_headers()
    {
        var response = await _factory.Client.GetAsync(new Uri("/api/health", UriKind.Relative));
        response.Headers.GetValues("X-Request-ID").Single().Should().MatchRegex("^[0-9a-f]{32}$");
        response.Headers.GetValues("X-Content-Type-Options").Single().Should().Be("nosniff");
        response.Headers.GetValues("X-Frame-Options").Single().Should().Be("DENY");
        response.Headers.GetValues("Referrer-Policy").Single().Should().Be("same-origin");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Theory]
    [InlineData("trace-123", "trace-123")]
    [InlineData("bad\u0001id", null)]
    public async Task An_incoming_request_id_is_kept_when_printable(string incoming, string? expected)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.TryAddWithoutValidation("X-Request-ID", incoming);
        var response = await _factory.Client.SendAsync(request);
        var echoed = response.Headers.GetValues("X-Request-ID").Single();
        if (expected is null)
        {
            echoed.Should().MatchRegex("^[0-9a-f]{32}$");
        }
        else
        {
            echoed.Should().Be(expected);
        }
    }

    [Fact]
    public async Task A_long_request_id_is_cut_to_64_characters()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("X-Request-ID", new string('a', 100));
        var response = await _factory.Client.SendAsync(request);
        response.Headers.GetValues("X-Request-ID").Single().Should().Be(new string('a', 64));
    }

    [Theory]
    [InlineData("/api/_test/unauthorized", 401, "session_expired", "Your session has expired. Please sign in again.")]
    [InlineData("/api/_test/invalid", 422, "validation_error", "End date must be on or after start date")]
    [InlineData("/api/_test/constraint", 409, "conflict", "The change conflicts with existing data.")]
    [InlineData("/api/_test/busy", 503, "database_unavailable", "The database is temporarily unavailable. Please retry.")]
    [InlineData("/api/_test/boom", 500, "internal_error", "An unexpected error occurred.")]
    [InlineData("/api/no-such-route", 404, "not_found", "Not Found")]
    public async Task Errors_use_the_envelope(string path, int status, string code, string message)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        request.Headers.Add("X-Request-ID", "req-1");
        var response = await _factory.Client.SendAsync(request);
        ((int)response.StatusCode).Should().Be(status);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        (await response.Content.ReadAsStringAsync()).Should().Be(
            $$"""{"error":{"code":"{{code}}","message":"{{message}}"},"request_id":"req-1"}""");
    }

    [Fact]
    public async Task Too_many_requests_sends_retry_after()
    {
        var response = await _factory.Client.GetAsync(new Uri("/api/_test/throttled", UriKind.Relative));
        ((int)response.StatusCode).Should().Be(429);
        response.Headers.RetryAfter!.Delta.Should().Be(TimeSpan.FromSeconds(900));
        (await Json(response)).GetProperty("error").GetProperty("code").GetString().Should().Be("too_many_requests");
    }

    [Fact]
    public async Task Wrong_method_is_405_in_the_envelope()
    {
        using var content = new StringContent("{}");
        var response = await _factory.Client.PostAsync(new Uri("/api/health", UriKind.Relative), content);
        response.StatusCode.Should().Be(HttpStatusCode.MethodNotAllowed);
        var error = (await Json(response)).GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("method_not_allowed");
        error.GetProperty("message").GetString().Should().Be("Method Not Allowed");
    }

    [Fact]
    public async Task Request_validation_is_422_with_pydantic_field_paths()
    {
        var response = await _factory.Client.GetAsync(new Uri("/api/_test/paged?page=0", UriKind.Relative));
        ((int)response.StatusCode).Should().Be(422);
        var error = (await Json(response)).GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("validation_error");
        error.GetProperty("details")[0].GetProperty("field").GetString().Should().Be("query.page");
        error.GetProperty("message").GetString().Should().StartWith("query.page: ");
    }

    [Fact]
    public async Task Cors_preflight_allows_the_frontend_origin_with_credentials()
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/health");
        request.Headers.Add("Origin", "http://localhost:5173");
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-csrf-token");
        var response = await _factory.Client.SendAsync(request);
        response.Headers.GetValues("Access-Control-Allow-Origin").Single().Should().Be("http://localhost:5173");
        response.Headers.GetValues("Access-Control-Allow-Credentials").Single().Should().Be("true");
        string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers")).ToLowerInvariant()
            .Should().Contain("x-csrf-token");

        using var other = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        other.Headers.Add("Origin", "https://evil.example");
        (await _factory.Client.SendAsync(other)).Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }

    [Fact]
    public async Task Simple_cors_responses_expose_the_download_headers()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/health");
        request.Headers.Add("Origin", "http://localhost:5173");
        var response = await _factory.Client.SendAsync(request);
        string.Join(",", response.Headers.GetValues("Access-Control-Expose-Headers"))
            .Should().Contain("Content-Disposition").And.Contain("X-Request-ID");
    }

    [Fact]
    public async Task Docs_are_served_where_fastapi_serves_them()
    {
        var openapi = await _factory.Client.GetAsync(new Uri("/api/openapi.json", UriKind.Relative));
        openapi.StatusCode.Should().Be(HttpStatusCode.OK);
        var doc = await Json(openapi);
        doc.GetProperty("info").GetProperty("title").GetString().Should().Be("Office Attendance Tracker API");
        doc.GetProperty("paths").TryGetProperty("/api/health", out _).Should().BeTrue();

        var swagger = await _factory.Client.GetAsync(new Uri("/api/docs/index.html", UriKind.Relative));
        swagger.StatusCode.Should().Be(HttpStatusCode.OK);
        swagger.Headers.CacheControl?.NoStore.Should().NotBe(true); // docs pages are cacheable, as in Python

        var redoc = await _factory.Client.GetAsync(new Uri("/api/redoc", UriKind.Relative));
        (await redoc.Content.ReadAsStringAsync()).Should().Contain("/api/openapi.json");
    }
}
