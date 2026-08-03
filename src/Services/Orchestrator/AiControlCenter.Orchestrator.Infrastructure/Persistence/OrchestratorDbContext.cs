using Microsoft.EntityFrameworkCore;

namespace AiControlCenter.Orchestrator.Infrastructure.Persistence;

public sealed class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orchestrator");
        base.OnModelCreating(modelBuilder);
    }
}
