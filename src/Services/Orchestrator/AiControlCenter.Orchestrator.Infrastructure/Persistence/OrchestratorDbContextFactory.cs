using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AiControlCenter.Orchestrator.Infrastructure.Persistence;

public sealed class OrchestratorDbContextFactory : IDesignTimeDbContextFactory<OrchestratorDbContext>
{
    public OrchestratorDbContext CreateDbContext(string[] args)
    {
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__OrchestratorDatabase")
            ?? throw new InvalidOperationException("ConnectionStrings__OrchestratorDatabase is required.");
        var options = new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(connectionString, npgsql =>
                npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orchestrator"))
            .Options;
        return new OrchestratorDbContext(options);
    }
}
