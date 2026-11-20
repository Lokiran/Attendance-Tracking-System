using Ats.Application.Configuration;
using Ats.Infrastructure.Database;
using Ats.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace Ats.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Database target, connectivity probe and the EF Core context (scoped per request).</summary>
    public static IServiceCollection AddAtsInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AtsSettings>();
            return DatabaseTarget.Parse(settings.DatabaseUrl, settings.BaseDirectory);
        });
        services.AddSingleton<IDatabaseProbe, DatabaseProbe>();
        services.AddDbContext<AtsDbContext>((sp, options) =>
            AtsDbContextOptions.Configure(options, sp.GetRequiredService<DatabaseTarget>()));
        return services;
    }
}
