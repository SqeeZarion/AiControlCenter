using AiControlCenter.ControlPlane.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AiControlCenter.ControlPlane.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddControlPlaneInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("ControlPlaneDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings:ControlPlaneDatabase is required.");

        services.AddDbContext<ControlPlaneDbContext>(options =>
            options.UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "control_plane")));

        return services;
    }
}
