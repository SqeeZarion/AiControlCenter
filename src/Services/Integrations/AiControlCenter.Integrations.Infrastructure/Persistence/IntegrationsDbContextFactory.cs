using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiControlCenter.Integrations.Infrastructure.Persistence;

public sealed class IntegrationsDbContextFactory : IDesignTimeDbContextFactory<IntegrationsDbContext>
{
    public IntegrationsDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__IntegrationsDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings__IntegrationsDatabase is required for design-time migrations.");
        var options = new DbContextOptionsBuilder<IntegrationsDbContext>()
            .UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "integrations"))
            .Options;
        return new IntegrationsDbContext(options);
    }
}
