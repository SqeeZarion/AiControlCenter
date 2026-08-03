using Microsoft.EntityFrameworkCore;

namespace AiControlCenter.Integrations.Infrastructure.Persistence;

public sealed class IntegrationsDbContext(DbContextOptions<IntegrationsDbContext> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("integrations");
        base.OnModelCreating(modelBuilder);
    }
}
