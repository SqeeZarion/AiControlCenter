using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiControlCenter.Integrations.Infrastructure.Persistence;

internal sealed class ProviderConnectionRepository(IntegrationsDbContext db) : IProviderConnectionRepository
{
    public async Task<IReadOnlyCollection<ProviderConnection>> ListAsync(Guid ownerUserId, CancellationToken token) =>
        await db.ProviderConnections.AsNoTracking().Where(x => x.OwnerUserId == ownerUserId)
            .OrderBy(x => x.Provider).ThenBy(x => x.DisplayName).ToArrayAsync(token);
    public Task<ProviderConnection?> GetAsync(Guid id, Guid ownerUserId, bool tracking, CancellationToken token)
    {
        var query = db.ProviderConnections.Where(x => x.Id == id && x.OwnerUserId == ownerUserId);
        return (tracking ? query : query.AsNoTracking()).SingleOrDefaultAsync(token);
    }
    public Task<ProviderConnection?> GetByProviderAsync(Guid ownerUserId, AiProviderKind provider,
        string displayName, CancellationToken token) => db.ProviderConnections.SingleOrDefaultAsync(
            connection => connection.OwnerUserId == ownerUserId && connection.Provider == provider
                && connection.DisplayName == displayName, token);
    public void Add(ProviderConnection connection) => db.ProviderConnections.Add(connection);
}

internal sealed class ProviderOAuthStateRepository(IntegrationsDbContext db) : IProviderOAuthStateRepository
{
    public Task<ProviderOAuthState?> GetByHashAsync(string stateHash, CancellationToken token) =>
        db.ProviderOAuthStates.SingleOrDefaultAsync(x => x.StateHash == stateHash, token);
    public void Add(ProviderOAuthState state) => db.ProviderOAuthStates.Add(state);
}

internal sealed class ProviderExecutionReceiptRepository(IntegrationsDbContext db)
    : IProviderExecutionReceiptRepository
{
    public Task<ProviderExecutionReceipt?> GetAsync(
        Guid ownerUserId,
        string idempotencyKey,
        CancellationToken token) =>
        db.ProviderExecutionReceipts.SingleOrDefaultAsync(
            receipt => receipt.OwnerUserId == ownerUserId && receipt.IdempotencyKey == idempotencyKey,
            token);
    public async Task<IReadOnlyCollection<ProviderExecutionReceipt>> ListCompletedAsync(
        Guid ownerUserId,
        DateTimeOffset since,
        CancellationToken token) => await db.ProviderExecutionReceipts.AsNoTracking()
            .Where(receipt => receipt.OwnerUserId == ownerUserId
                && receipt.Status != ProviderExecutionReceiptStatus.Pending
                && receipt.CompletedAt >= since)
            .OrderBy(receipt => receipt.CompletedAt)
            .ToArrayAsync(token);
    public void Add(ProviderExecutionReceipt receipt) => db.ProviderExecutionReceipts.Add(receipt);
}
