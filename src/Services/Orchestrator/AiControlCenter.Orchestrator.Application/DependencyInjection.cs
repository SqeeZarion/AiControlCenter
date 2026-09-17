using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AiControlCenter.Orchestrator.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddOrchestratorApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddValidatorsFromAssemblyContaining<AgentRunApplicationService>();
        services.AddScoped<AgentRunApplicationService>();
        return services;
    }
}
