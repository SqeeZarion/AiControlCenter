using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AiControlCenter.Integrations.Infrastructure.Persistence;

public sealed class IntegrationsDbContext(DbContextOptions<IntegrationsDbContext> options)
    : DbContext(options), IIntegrationsUnitOfWork
{
    public DbSet<ProviderConnection> ProviderConnections => Set<ProviderConnection>();
    public DbSet<ProviderOAuthState> ProviderOAuthStates => Set<ProviderOAuthState>();
    public DbSet<ProviderExecutionReceipt> ProviderExecutionReceipts => Set<ProviderExecutionReceipt>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("integrations");
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(IntegrationsDbContext).Assembly);
        base.OnModelCreating(modelBuilder);
    }
}
