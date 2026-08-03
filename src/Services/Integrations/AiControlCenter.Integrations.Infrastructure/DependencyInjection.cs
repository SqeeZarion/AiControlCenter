using AiControlCenter.Integrations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiControlCenter.Integrations.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIntegrationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("IntegrationsDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:IntegrationsDatabase is required.");

        services.AddDbContext<IntegrationsDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "integrations")));

        return services;
    }
}
