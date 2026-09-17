using AiControlCenter.ControlPlane.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiControlCenter.ControlPlane.Infrastructure.Persistence;

public sealed class ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options) : DbContext(options)
{
    public DbSet<Direction> Directions => Set<Direction>();
    public DbSet<AgentDefinition> AgentDefinitions => Set<AgentDefinition>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("control_plane");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(ControlPlaneDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
