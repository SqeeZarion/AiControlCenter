using AiControlCenter.Identity.Application;
using AiControlCenter.Identity.Infrastructure.Persistence;
using AiControlCenter.Identity.Infrastructure.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiControlCenter.Identity.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddIdentityInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("IdentityDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:IdentityDatabase is required.");

        services.AddDbContext<IdentityDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "identity")));

        services.AddScoped<IUserRepository, UserRepository>();
        services.AddScoped<IRoleRepository, RoleRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IIdentityUnitOfWork, IdentityUnitOfWork>();
        services.AddSingleton<IPasswordHasher, PasswordHasherAdapter>();
        services.AddSingleton<IRefreshTokenGenerator, RefreshTokenGenerator>();
        services.AddSingleton(TimeProvider.System);
        services.AddOptions<JwtIssuerOptions>()
            .Bind(configuration.GetSection(JwtIssuerOptions.SectionName));
        services.AddSingleton<IAccessTokenIssuer, RsaAccessTokenIssuer>();

        return services;
    }
}
