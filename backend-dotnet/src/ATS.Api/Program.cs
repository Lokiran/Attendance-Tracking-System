using Ats.Api.Setup;
using Ats.Application.Common;
using Ats.Application.Configuration;
using Ats.Infrastructure;
using Ats.Infrastructure.Database;

// Office Attendance Tracker API (ASP.NET Core port of backend/, see docs/dotnet-migration-plan.md).
try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddAtsConfiguration();
    builder.AddAtsLogging();
    builder.Services.AddSingleton<IClock, SystemClock>();
    builder.Services.AddAtsInfrastructure();
    builder.Services.AddAtsApi();

    var app = builder.Build();
    StartupChecks.Run(app);
    app.UseAtsPipeline();
    await app.RunAsync().ConfigureAwait(false);
    return 0;
}
catch (Exception ex) when (ex is SettingsException or DatabaseNotReadyException)
{
    // A configuration problem: say what is wrong, without a stack trace, and refuse to start.
    await Console.Error.WriteLineAsync($"Startup refused: {ex.Message}").ConfigureAwait(false);
    return 1;
}

/// <summary>Entry point (public for WebApplicationFactory in the tests).</summary>
public partial class Program;
