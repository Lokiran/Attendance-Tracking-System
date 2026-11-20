using Ats.Application.Configuration;
using Ats.Infrastructure.Database;

namespace Ats.Api.Setup;

internal static partial class StartupChecks
{
    /// <summary>
    /// Fail fast before serving requests (the Python app refuses to start the same way): settings are built and
    /// validated, the database URL is parsed, a SQLite file must already exist.
    /// </summary>
    public static void Run(WebApplication app)
    {
        var (settings, target) = LogConfiguration(app);
        DatabaseStartup.EnsureReady(target, settings);
    }

    /// <summary>Builds and validates the settings, parses the database URL and logs where the data is.</summary>
    public static (AtsSettings Settings, DatabaseTarget Target) LogConfiguration(WebApplication app)
    {
        var settings = app.Services.GetRequiredService<AtsSettings>();
        var target = app.Services.GetRequiredService<DatabaseTarget>();
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("ats");
        foreach (var warning in app.Services.GetRequiredService<StartupMessages>().Warnings)
        {
            LogConfigWarning(logger, warning);
        }

        LogDatabase(logger, target.Description, settings.EnvFile ?? "none");
        return (settings, target);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "{Warning}")]
    private static partial void LogConfigWarning(ILogger logger, string warning);

    [LoggerMessage(Level = LogLevel.Information, Message = "database: {Database} (env file: {EnvFile})")]
    private static partial void LogDatabase(ILogger logger, string database, string envFile);
}
