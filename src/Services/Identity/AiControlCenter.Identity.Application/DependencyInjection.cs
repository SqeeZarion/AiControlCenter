using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace AiControlCenter.Identity.Application;

//Цей extension method реєструє Application-компоненти в DI-контейнері. Валідатори
public static class DependencyInjection
{
    public static IServiceCollection AddIdentityApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssemblyContaining<IdentityApplicationMarker>();
        services.AddScoped<IdentityApplicationService>();
        return services;
    }
}
