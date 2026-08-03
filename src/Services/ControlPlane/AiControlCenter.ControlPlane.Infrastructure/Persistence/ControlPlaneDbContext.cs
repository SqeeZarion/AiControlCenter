using Microsoft.EntityFrameworkCore;

namespace AiControlCenter.ControlPlane.Infrastructure.Persistence;

public sealed class ControlPlaneDbContext(DbContextOptions<ControlPlaneDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("control_plane");
        base.OnModelCreating(modelBuilder);
    }
}
