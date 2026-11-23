using System.Globalization;
using Ats.Application.Audit;
using Ats.Application.Auth;
using Ats.Application.Common;
using Ats.Application.Configuration;
using Ats.Application.Data;
using Ats.Application.Seed;
using Ats.Application.Wfo;
using Ats.Domain;
using Ats.Domain.Entities;
using Ats.Domain.Rules;
using Ats.Infrastructure.Database;
using Microsoft.EntityFrameworkCore;

namespace Ats.Api.Setup;

/// <summary>
/// Management commands (<c>app/cli.py</c>), run with the API's own configuration and services, no web server:
/// <code>
/// ATS.Api seed [--reset-demo] [--reset-passwords]
/// ATS.Api create-admin --email admin@company.com --name "Jane Doe" --code ADM100
/// ATS.Api wfo-refresh --from YYYY-MM-DD --to YYYY-MM-DD
/// </code>
/// SEED_* and ATS_ADMIN_PASSWORD are read from the environment, then from the selected env file (as Python's load_dotenv).
/// </summary>
internal static class Cli
{
    public static readonly string[] Names = ["seed", "create-admin", "wfo-refresh"];
    private static readonly string[] AdminOptions = ["--email", "--name", "--code"];
    private static readonly string[] RefreshOptions = ["--from", "--to"];

    public static async Task<int> RunAsync(WebApplication app, string[] args)
    {
        var settings = app.Services.GetRequiredService<AtsSettings>();
        var target = app.Services.GetRequiredService<DatabaseTarget>();
        Console.WriteLine($"Database: {target.Description}  (env file: {settings.EnvFile ?? "none"})");
        try
        {
            DatabaseStartup.EnsureReady(target, settings);
        }
        catch (DatabaseNotReadyException e)
        {
            await Console.Error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }

        var fileValues = settings.EnvFile is { } path && File.Exists(path) ? EnvFile.Load(path) : new Dictionary<string, string>();
        string? Env(string key) => Environment.GetEnvironmentVariable(key) ?? fileValues.GetValueOrDefault(key);
        using var scope = app.Services.CreateScope();
        var services = scope.ServiceProvider;
        var options = Options(args.Skip(1).ToList());
        return args[0] switch
        {
            "seed" => await SeedAsync(services, Env, options).ConfigureAwait(false),
            "create-admin" => await CreateAdminAsync(services, Env, options).ConfigureAwait(false),
            _ => await WfoRefreshAsync(services, options).ConfigureAwait(false),
        };
    }

    private static Dictionary<string, string?> Options(List<string> args)
    {
        var map = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Count; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                continue;
            }

            var eq = a.IndexOf('=', StringComparison.Ordinal);
            if (eq > 0)
            {
                map[a[..eq]] = a[(eq + 1)..];
            }
            else if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                map[a] = args[++i];
            }
            else
            {
                map[a] = null;
            }
        }

        return map;
    }

    private static async Task<int> Missing(string command, params string[] names)
    {
        await Console.Error.WriteLineAsync($"{command}: the following arguments are required: {string.Join(", ", names)}").ConfigureAwait(false);
        return 2;
    }

    private static async Task<int> SeedAsync(IServiceProvider services, Func<string, string?> env, Dictionary<string, string?> options)
    {
        try
        {
            var result = await services.GetRequiredService<DemoSeed>()
                .RunAsync(env, options.ContainsKey("--reset-demo"), options.ContainsKey("--reset-passwords")).ConfigureAwait(false);
            Console.WriteLine($"Seed complete: {result}");
            return 0;
        }
        catch (Exception e) when (e is SeedException or AppException)
        {
            await Console.Error.WriteLineAsync($"Seed failed: {e.Message}").ConfigureAwait(false);
            return 1;
        }
    }

    /// <summary>Reads a password without echoing it (or a line when the input is redirected).</summary>
    private static string ReadSecret(string prompt)
    {
        Console.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? "";
        }

        var chars = new List<char>();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.WriteLine();
                return new string([.. chars]);
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (chars.Count > 0)
                {
                    chars.RemoveAt(chars.Count - 1);
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                chars.Add(key.KeyChar);
            }
        }
    }

    private static async Task<int> CreateAdminAsync(IServiceProvider services, Func<string, string?> env, Dictionary<string, string?> options)
    {
        var missing = AdminOptions.Where(k => options.GetValueOrDefault(k) is null).ToArray();
        if (missing.Length > 0)
        {
            return await Missing("create-admin", missing).ConfigureAwait(false);
        }

        var fromEnv = env("ATS_ADMIN_PASSWORD");
        var password = string.IsNullOrEmpty(fromEnv) ? ReadSecret("New admin password: ") : fromEnv;
        if (string.IsNullOrEmpty(fromEnv) && ReadSecret("Repeat password: ") != password)
        {
            await Console.Error.WriteLineAsync("Passwords do not match").ConfigureAwait(false);
            return 1;
        }

        try
        {
            PasswordPolicy.Validate(password);
        }
        catch (AppException e)
        {
            await Console.Error.WriteLineAsync(e.Message).ConfigureAwait(false);
            return 1;
        }

        var db = services.GetRequiredService<IAtsDb>();
        var email = options["--email"]!.Trim().ToLowerInvariant();
        var code = options["--code"]!.ToUpperInvariant();
#pragma warning disable CA1304, CA1311, CA1862 // translated to SQL upper(), as in Python
        if (await db.Users.AnyAsync(u => u.Email == email || u.EmployeeCode.ToUpper() == code).ConfigureAwait(false))
#pragma warning restore CA1304, CA1311, CA1862
        {
            await Console.Error.WriteLineAsync("A user with this email or employee code already exists").ConfigureAwait(false);
            return 1;
        }

        var settings = services.GetRequiredService<AtsSettings>();
        var clock = services.GetRequiredService<IClock>();
        var user = new User
        {
            EmployeeCode = code, FullName = options["--name"]!, Email = email, PasswordHash = services.GetRequiredService<IPasswordHasher>().Hash(password),
            Role = Role.Admin, IsActive = true, AttendanceTracked = false, DateJoined = OfficeTime.LocalDate(clock.UtcNow, settings.TimeZone),
            JobTitle = "Administrator",
        };
        await using (var tx = await db.Database.BeginTransactionAsync().ConfigureAwait(false))
        {
            db.Users.Add(user);
            await db.SaveChangesAsync().ConfigureAwait(false);
            AuditTrail.Record(db, null, "ADMIN_BOOTSTRAPPED", "user", user.Id,
                              after: new Dictionary<string, object?> { ["email"] = email, ["employee_code"] = user.EmployeeCode }, ctx: new RequestContext("cli"));
            await db.SaveChangesAsync().ConfigureAwait(false);
            await tx.CommitAsync().ConfigureAwait(false);
        }

        Console.WriteLine($"Administrator {email} created");
        return 0;
    }

    /// <summary>Recompute and store the WFO classification for a date range (after schedule / holiday changes).</summary>
    private static async Task<int> WfoRefreshAsync(IServiceProvider services, Dictionary<string, string?> options)
    {
        var missing = RefreshOptions.Where(k => options.GetValueOrDefault(k) is null).ToArray();
        if (missing.Length > 0)
        {
            return await Missing("wfo-refresh", missing).ConfigureAwait(false);
        }

        if (!DateOnly.TryParseExact(options["--from"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var start)
            || !DateOnly.TryParseExact(options["--to"], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var end))
        {
            await Console.Error.WriteLineAsync("--from and --to must be dates like 2026-10-31").ConfigureAwait(false);
            return 2;
        }

        if (end < start)
        {
            await Console.Error.WriteLineAsync("--to must be on or after --from").ConfigureAwait(false);
            return 2;
        }

        var db = services.GetRequiredService<IAtsDb>();
        var people = await db.Users.Where(u => u.AttendanceTracked).OrderBy(u => u.Id).ToListAsync().ConfigureAwait(false);
        var written = await services.GetRequiredService<WfoService>().RefreshRangeAsync(people, start, end).ConfigureAwait(false);
        await db.SaveChangesAsync().ConfigureAwait(false);
        Console.WriteLine($"WFO refreshed for {people.Count} people, {start:yyyy-MM-dd} to {end:yyyy-MM-dd}: {written} day record(s) written");
        return 0;
    }
}
