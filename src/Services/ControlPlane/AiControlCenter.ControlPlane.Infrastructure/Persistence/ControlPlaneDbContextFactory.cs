using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiControlCenter.ControlPlane.Infrastructure.Persistence;

public sealed class ControlPlaneDbContextFactory : IDesignTimeDbContextFactory<ControlPlaneDbContext>
{
    public ControlPlaneDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__ControlPlaneDatabase")
            ?? throw new InvalidOperationException(
                "ConnectionStrings__ControlPlaneDatabase is required for design-time ControlPlane migrations.");
        var options = new DbContextOptionsBuilder<ControlPlaneDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "control_plane"))
            .Options;
        return new ControlPlaneDbContext(options);
    }
}
