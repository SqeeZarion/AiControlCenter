using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AiControlCenter.Identity.Application;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<IdentityApplicationMarker>();
        services.AddScoped<IdentityApplicationService>();
        return services;
    }
}
