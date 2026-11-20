using System.Net.Http.Json;
using System.Text.Json;
using Ats.Api.Auth;
using Ats.Application.Common;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Infrastructure.Persistence;
using Ats.Tests.Persistence;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;

namespace Ats.Tests.Auth;

/// <summary>The people of the Python suite's <c>org</c> fixture.</summary>
public sealed record Org(User Admin, User Vikram, User Priya, User Anil, User Rahul, User Sneha, User Arjun,
                         Department Ai, Department Se);

/// <summary>
/// The API on a fresh migrated database with the Python suite's <c>org</c>: admin; Vikram (manager) -> Priya, Anil;
/// Priya -> Rahul, Sneha; Anil -> Arjun. Every password is <see cref="Password"/>, hashed by argon2-cffi in Python.
/// </summary>
public sealed class AuthHost : IDisposable
{
    public const string Password = "Str0ngPassw0rd!";

    /// <summary>argon2-cffi <c>PasswordHasher().hash(Password)</c>, made by the Python backend.</summary>
    public const string PythonHash = "$argon2id$v=19$m=65536,t=3,p=4$rqysI36y7YVoFMZBVlbqWw$+h0quwSAbnKfIxa+/E9zRiKDi5zATNWhtLgHieeZcns";

    public static readonly DateTimeOffset Start = new(2026, 3, 12, 4, 30, 0, TimeSpan.Zero);

    public AuthHost(IDictionary<string, string?>? settings = null, Action<IServiceCollection>? services = null)
    {
        Clock = new FakeClock(Start);
        Api = new ApiFactory(settings, s =>
        {
            s.AddSingleton<IClock>(Clock);
            s.AddControllers().AddApplicationPart(typeof(PolicyProbeController).Assembly);
            services?.Invoke(s);
        });
        Org = Seed();
        Client = Api.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    public ApiFactory Api { get; }

    public FakeClock Clock { get; }

    public Org Org { get; }

    public HttpClient Client { get; }

    /// <summary>A database context on the host's database (a new scope each time).</summary>
    public AtsDbContext Db() => Api.Services.CreateScope().ServiceProvider.GetRequiredService<AtsDbContext>();

    /// <summary>Signs in and returns the CSRF token for state-changing requests (Python: login()).</summary>
    public async Task<string> LoginAsync(User user, string password = Password)
    {
        var r = await Client.PostAsJsonAsync("/api/auth/login", new { email = user.Email, password });
        r.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("csrf_token").GetString()!;
    }

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null, string? csrf = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body ?? new { }) };
        if (csrf is not null)
        {
            request.Headers.Add("X-CSRF-Token", csrf);
        }

        return Client.SendAsync(request);
    }

    private Org Seed()
    {
        using var db = Db();
        db.WorkSchedules.Add(new WorkSchedule
        {
            Name = "General", StartTime = new TimeOnly(9, 30), EndTime = new TimeOnly(18, 30), RequiredMinutes = 480,
            WorkingDays = [1, 2, 3, 4, 5], EffectiveFrom = new DateOnly(2024, 1, 1),
        });
        var ai = new Department { Name = "AI Engineering" };
        var se = new Department { Name = "Software Engineering" };
        db.Departments.AddRange(ai, se);
        db.SaveChanges();
        var n = 0;
        User Person(string name, Role role = Role.Employee, User? manager = null, Department? dept = null)
        {
            var u = new User
            {
                EmployeeCode = $"E{++n:000}", FullName = name, Email = $"{name.ToLowerInvariant().Replace(' ', '.')}@test.example",
                PasswordHash = PythonHash, Role = role, ManagerId = manager?.Id, DepartmentId = dept?.Id,
                DateJoined = new DateOnly(2025, 1, 1),
            };
            db.Users.Add(u);
            db.SaveChanges();
            return u;
        }

        var admin = Person("Admin User", Role.Admin);
        var vikram = Person("Vikram Desai", Role.Manager);
        var priya = Person("Priya Sharma", Role.Manager, vikram, ai);
        var anil = Person("Anil Kumar", Role.Manager, vikram, se);
        var rahul = Person("Rahul Reddy", manager: priya, dept: ai);
        var sneha = Person("Sneha Rao", manager: priya, dept: ai);
        var arjun = Person("Arjun Kumar", manager: anil, dept: se);
        return new Org(admin, vikram, priya, anil, rahul, sneha, arjun, ai, se);
    }

    public void Dispose()
    {
        Client.Dispose();
        Api.Dispose();
    }
}

/// <summary>Endpoints guarded by each policy (test assembly only).</summary>
[ApiController]
[Route("api/_policy")]
public sealed class PolicyProbeController : ControllerBase
{
    [HttpGet("user")]
    [Authorize(Policy = SessionAuth.User)]
    public IActionResult AnyUser() => Ok(new { id = HttpContext.Auth().User.Id });

    [HttpGet("team")]
    [Authorize(Policy = SessionAuth.ManagerOrAdmin)]
    public IActionResult Team() => Ok(new { ok = true });

    [HttpGet("admin")]
    [Authorize(Policy = SessionAuth.Admin)]
    public IActionResult AdminOnly() => Ok(new { ok = true });

    [HttpPost("admin")]
    [Authorize(Policy = SessionAuth.Admin)]
    public IActionResult AdminWrite() => Ok(new { ok = true });
}
