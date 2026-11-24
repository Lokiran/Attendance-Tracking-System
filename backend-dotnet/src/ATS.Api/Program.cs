using Ats.Api.Setup;
using Ats.Application.Common;
using Ats.Application.Configuration;
using Ats.Infrastructure;
using Ats.Infrastructure.Database;
using Ats.Infrastructure.Persistence;

// Office Attendance Tracker API (ASP.NET Core; port of the former Python backend, see backend-dotnet/README.md).
try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.AddAtsConfiguration();
    builder.AddAtsLogging();
    builder.Services.AddSingleton<IClock, SystemClock>();
    builder.Services.AddAtsInfrastructure();
    builder.Services.AddAtsApi();

    var app = builder.Build();
    if (args.Length > 0 && DatabaseCommands.Names.Contains(args[0]))
    {
        // Database maintenance: migrate | ef-baseline | schema-check | schema-catalog [file]. No web server.
        StartupChecks.LogConfiguration(app);
        var commands = new DatabaseCommands(app.Services.GetRequiredService<DatabaseTarget>(), Console.Out);
        return await commands.RunAsync(args[0], args.Skip(1).ToList()).ConfigureAwait(false);
    }

    if (args.Length > 0 && Cli.Names.Contains(args[0]))
    {
        // Management commands: seed | create-admin | wfo-refresh. No web server.
        return await Cli.RunAsync(app, args).ConfigureAwait(false);
    }

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
