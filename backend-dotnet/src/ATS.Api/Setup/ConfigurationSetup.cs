using Ats.Application.Configuration;
using Serilog;
using Serilog.Events;

namespace Ats.Api.Setup;

/// <summary>Messages produced while the settings were built, logged once the logger exists.</summary>
internal sealed class StartupMessages
{
    public List<string> Warnings { get; } = [];
}

internal static class ConfigurationSetup
{
    /// <summary>
    /// Settings come from one explicitly selected env file plus environment variables (which win), with the same
    /// key names as the Python backend. They are built lazily from the final configuration, so test hosts can
    /// override any key.
    /// </summary>
    public static WebApplicationBuilder AddAtsConfiguration(this WebApplicationBuilder builder)
    {
        var environment = EnvironmentInfo.Select();
        builder.Configuration.AddInMemoryCollection(
            environment.Values.Select(kv => new KeyValuePair<string, string?>(kv.Key, kv.Value)));
        builder.Configuration.AddEnvironmentVariables(); // environment variables override the env file

        builder.Services.AddSingleton(environment);
        builder.Services.AddSingleton<StartupMessages>();
        builder.Services.AddSingleton(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var messages = sp.GetRequiredService<StartupMessages>();
            return SettingsLoader.Load(key => configuration[key], environment.BaseDirectory, environment.EnvFile,
                                       messages.Warnings.Add);
        });
        return builder;
    }

    /// <summary>Serilog with the Python log format; level from LOG_LEVEL (DEBUG, INFO, WARNING, ERROR, CRITICAL).</summary>
    public static WebApplicationBuilder AddAtsLogging(this WebApplicationBuilder builder)
    {
        builder.Services.AddSerilog((services, logger) => logger
            .MinimumLevel.Is(Level(services.GetRequiredService<IConfiguration>()["LOG_LEVEL"]))
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            // The base authentication handler logs every anonymous request ("was not authenticated") at Information.
            .MinimumLevel.Override("Ats.Api.Auth.SessionAuthenticationHandler", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Console(outputTemplate:
                "{Timestamp:yyyy-MM-dd HH:mm:ss,fff} {Level:u4} {SourceContext} {Message:lj}{NewLine}{Exception}",
                formatProvider: System.Globalization.CultureInfo.InvariantCulture));
        return builder;
    }

    internal static LogEventLevel Level(string? pythonLevel) => (pythonLevel ?? "INFO").Trim().ToUpperInvariant() switch
    {
        "DEBUG" or "10" => LogEventLevel.Debug,
        "WARNING" or "WARN" or "30" => LogEventLevel.Warning,
        "ERROR" or "40" => LogEventLevel.Error,
        "CRITICAL" or "FATAL" or "50" => LogEventLevel.Fatal,
        _ => LogEventLevel.Information,
    };
}
