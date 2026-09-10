using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AiControlCenter.ControlPlane.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddControlPlaneApplication(this IServiceCollection services)
    {
        services.AddSingleton(TimeProvider.System);
        services.AddValidatorsFromAssemblyContaining<DirectionApplicationService>();
        services.AddScoped<DirectionApplicationService>();
        return services;
    }
}
