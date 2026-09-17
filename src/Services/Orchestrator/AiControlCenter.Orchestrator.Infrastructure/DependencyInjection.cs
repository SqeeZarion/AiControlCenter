using AiControlCenter.Orchestrator.Application;
using AiControlCenter.Orchestrator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiControlCenter.Orchestrator.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddOrchestratorInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("OrchestratorDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:OrchestratorDatabase is required.");

        services.AddDbContext<OrchestratorDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orchestrator")));
        services.AddScoped<IAgentRunRepository, AgentRunRepository>();
        services.AddScoped<IOrchestratorUnitOfWork, OrchestratorUnitOfWork>();

        return services;
    }
}
