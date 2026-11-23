using Ats.Application.Admin;
using Ats.Application.Attendance;
using Ats.Application.Devices;
using Ats.Application.Auth;
using Ats.Application.Dashboard;
using Ats.Application.Reference;
using Ats.Application.Uploads;
using Ats.Application.Wfo;
using Ats.Application.Configuration;
using Ats.Application.Data;
using Ats.Application.Scope;
using Ats.Infrastructure.Security;
using FluentValidation;
using Ats.Infrastructure.Database;
using Ats.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ats.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Database (target, probe, EF Core context), sign-in services, scope rules and request validators.</summary>
    public static IServiceCollection AddAtsInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AtsSettings>();
            return DatabaseTarget.Parse(settings.DatabaseUrl, settings.BaseDirectory);
        });
        services.AddSingleton<IDatabaseProbe, DatabaseProbe>();
        services.AddSingleton<RecordCache>();
        services.AddSingleton<RecordCacheInvalidator>();
        services.AddDbContext<AtsDbContext>((sp, options) =>
        {
            AtsDbContextOptions.Configure(options, sp.GetRequiredService<DatabaseTarget>());
            options.AddInterceptors(sp.GetRequiredService<RecordCacheInvalidator>());
        });
        services.AddScoped<IAtsDb>(sp => sp.GetRequiredService<AtsDbContext>());

        // Sign-in: Argon2id passwords, sessions, Entra ID (network calls behind IEntraHttp, 10 s timeout like Python).
        services.AddSingleton<IPasswordHasher, Argon2PasswordHasher>();
        services.AddHttpClient<IEntraHttp, EntraHttp>(c => c.Timeout = TimeSpan.FromSeconds(10));
        services.AddScoped<IEntraSignIn, EntraSignIn>();
        services.AddScoped<AuthService>();
        services.AddScoped<ScopeService>();

        // Attendance, WFO and dashboard reads (rules engines in Domain, bulk loading and the shared record cache).
        services.AddScoped<AttendanceData>();
        services.AddScoped<AttendanceService>();
        services.AddScoped<WfoService>();
        services.AddScoped<DashboardService>();
        services.AddScoped<ReferenceService>();
        services.AddSingleton(sp => WfoMapping.Load(sp.GetRequiredService<AtsSettings>())); // read once, like Python's lru_cache
        services.AddScoped<WfoUploadService>();
        services.AddScoped<AdminUserService>();
        services.AddScoped<ConfigurationService>();
        services.AddScoped<DeviceService>();
        services.AddScoped<Ats.Application.Reports.ReportService>();
        services.AddScoped<Ats.Application.Seed.DemoSeed>();
        AddValidators(services, typeof(AuthService).Assembly);
        return services;
    }

    /// <summary>Every FluentValidation validator in the assembly, as a singleton <c>IValidator&lt;T&gt;</c>.</summary>
    private static void AddValidators(IServiceCollection services, System.Reflection.Assembly assembly)
    {
        foreach (var type in assembly.GetTypes().Where(t => t is { IsClass: true, IsAbstract: false }))
        {
            foreach (var contract in type.GetInterfaces()
                         .Where(i => i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IValidator<>)))
            {
                services.AddSingleton(contract, type);
            }
        }
    }
}
