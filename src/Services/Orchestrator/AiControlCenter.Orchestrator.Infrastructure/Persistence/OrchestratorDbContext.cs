using AiControlCenter.Orchestrator.Domain;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace AiControlCenter.Orchestrator.Infrastructure.Persistence;

public sealed class OrchestratorDbContext(DbContextOptions<OrchestratorDbContext> options) : DbContext(options)
{
    public DbSet<AgentRun> AgentRuns => Set<AgentRun>();
    public DbSet<RunStep> RunSteps => Set<RunStep>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("orchestrator");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrchestratorDbContext).Assembly);
        modelBuilder.AddInboxStateEntity();
        modelBuilder.AddOutboxMessageEntity();
        modelBuilder.AddOutboxStateEntity();
        base.OnModelCreating(modelBuilder);
    }
}
