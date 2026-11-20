using Ats.Application.Configuration;
using Ats.Infrastructure.Database;
using Microsoft.Extensions.DependencyInjection;

namespace Ats.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Database target and probe. EF Core (<c>AtsDbContext</c>) is added in phase 2.</summary>
    public static IServiceCollection AddAtsInfrastructure(this IServiceCollection services)
    {
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AtsSettings>();
            return DatabaseTarget.Parse(settings.DatabaseUrl, settings.BaseDirectory);
        });
        services.AddSingleton<IDatabaseProbe, DatabaseProbe>();
        return services;
    }
}
