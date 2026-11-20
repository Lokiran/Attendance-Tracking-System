using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Ats.Tests;

internal static class TestEnvironment
{
    /// <summary>
    /// Tests never read a developer's env file (the Python conftest does the same): ATS_ENV_FILE points at an
    /// empty file before any host starts.
    /// </summary>
#pragma warning disable CA2255 // a module initializer is exactly what a test assembly needs here
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Init()
    {
        var empty = Path.Combine(Path.GetTempPath(), "ats-tests-empty.env");
        File.WriteAllText(empty, "");
        Environment.SetEnvironmentVariable("ATS_ENV_FILE", empty);
    }

    private static readonly Lazy<string> Template = new(() =>
    {
        var path = Path.Combine(Path.GetTempPath(), $"ats-template-{Guid.NewGuid():N}.sqlite3");
        var target = Ats.Infrastructure.Database.DatabaseTarget.Parse("sqlite:///" + path.Replace('\\', '/'), Path.GetTempPath());
        var code = new Ats.Infrastructure.Persistence.DatabaseCommands(target, TextWriter.Null).MigrateAsync().GetAwaiter().GetResult();
        return code == 0 ? path : throw new InvalidOperationException("Could not create the template database");
    });

    /// <summary>A new SQLite database with the application schema (a copy of one migrated template).</summary>
    public static string NewSqliteFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ats-test-{Guid.NewGuid():N}.sqlite3");
        File.Copy(Template.Value, path);
        return path;
    }
}

/// <summary>The API in-process, with settings given as the same keys the env file uses.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly Dictionary<string, string?> _settings;

    public ApiFactory(IDictionary<string, string?>? overrides = null)
    {
        _settings = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["APP_ENV"] = "test",
            ["SECRET_KEY"] = "test-only-key-0123456789-abcdefghijklmnopqrstuvwxyz",
            ["DATABASE_URL"] = "sqlite:///" + TestEnvironment.NewSqliteFile().Replace('\\', '/'),
            ["OFFICE_TIMEZONE"] = "Asia/Kolkata",
            ["CORS_ORIGINS"] = "http://localhost:5173",
        };
        foreach (var (key, value) in overrides ?? new Dictionary<string, string?>())
        {
            _settings[key] = value;
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(_settings));
    }
}
