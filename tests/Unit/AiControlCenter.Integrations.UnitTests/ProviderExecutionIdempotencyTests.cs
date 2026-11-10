using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;

namespace AiControlCenter.Integrations.UnitTests;

public sealed class ProviderExecutionIdempotencyTests
{
    [Fact]
    public async Task CompletedReceiptReturnsSameResultWithoutSecondProviderCall()
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(
            owner, AiProviderKind.DeterministicTest, "Deterministic", null, null, DateTimeOffset.UtcNow);
        var connections = new ConnectionRepository(connection);
        var receipts = new ReceiptRepository();
        var provider = new CountingProvider();
        var service = CreateService(connections, receipts, provider);
        var request = new AiExecutionRequest(
            owner, connection.Id, "deterministic-v1", "system", "input", [], "success", "run:attempt:1");

        var first = await service.ExecuteAsync(request, CancellationToken.None);
        var duplicate = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.True(first.Succeeded);
        Assert.Equal(first.Output, duplicate.Output);
        Assert.Equal(first.ProviderRequestId, duplicate.ProviderRequestId);
        Assert.Equal(1, provider.CallCount);
    }

    [Fact]
    public async Task PendingReceiptIsAmbiguousAndMustNotBlindRetry()
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(
            owner, AiProviderKind.DeterministicTest, "Deterministic", null, null, DateTimeOffset.UtcNow);
        var connections = new ConnectionRepository(connection);
        var receipts = new ReceiptRepository();
        receipts.Add(ProviderExecutionReceipt.Start(owner, connection.Id, "run:attempt:1", DateTimeOffset.UtcNow));
        var provider = new CountingProvider();
        var service = CreateService(connections, receipts, provider);

        var result = await service.ExecuteAsync(new AiExecutionRequest(
            owner, connection.Id, "deterministic-v1", "system", "input", [], "success", "run:attempt:1"),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(ProviderFailureKind.AmbiguousTimeout, result.FailureKind);
        Assert.False(ProviderFallbackPolicy.CanFallback(result.FailureKind));
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task AmbiguousProviderOutcomeLeavesTheNonPaidReceiptAmbiguous()
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(
            owner, AiProviderKind.DeterministicTest, "Deterministic", null, null, DateTimeOffset.UtcNow);
        var receipts = new ReceiptRepository();
        var provider = new CountingProvider
        {
            Result = new(false, null, ProviderFailureKind.AmbiguousTimeout,
                "generation_outcome_ambiguous", null, 0, 0, null, [],
                OutboundRequestCount: 1, Operation: "GenerationDispatch",
                GenerationDispatched: "unknown"),
        };
        var service = CreateService(new ConnectionRepository(connection), receipts, provider);

        var result = await service.ExecuteAsync(new AiExecutionRequest(
            owner, connection.Id, "deterministic-v1", "system", "input", [], null, "run:ambiguous"),
            CancellationToken.None);

        Assert.Equal(ProviderFailureKind.AmbiguousTimeout, result.FailureKind);
        Assert.Equal(ProviderExecutionReceiptStatus.Ambiguous, Assert.Single(receipts.Items).Status);
    }

    [Fact]
    public async Task LegacyLocalCliReceiptWithoutStageMarkersBlocksResumeWithoutMutation()
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(
            owner, AiProviderKind.DeterministicTest, "Deterministic", null, null, DateTimeOffset.UtcNow);
        var receipts = new ReceiptRepository();
        var receipt = ProviderExecutionReceipt.Start(owner, connection.Id, "legacy-local-cli",
            DateTimeOffset.UtcNow, AiProviderKind.GitHubCopilotLocalCli);
        receipt.Complete("""{"Succeeded":false,"ErrorCode":"local_copilot_cli_unavailable"}""",
            DateTimeOffset.UtcNow, actualRequestCount: 1);
        receipts.Add(receipt);
        var service = CreateService(new ConnectionRepository(connection), receipts, new CountingProvider());

        var result = await service.InspectResumeSafetyAsync(owner,
            AiProviderKind.GitHubCopilotLocalCli, receipt.IdempotencyKey, CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("LegacyAmbiguousOutcome", result.Code);
        Assert.Equal(ProviderExecutionReceiptStatus.Completed, receipt.Status);
        Assert.Equal(1, receipt.ActualRequestCount);
    }

    [Fact]
    public async Task MarkedPreDispatchLocalCliFailureRemainsEligibleForProviderPreflight()
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(
            owner, AiProviderKind.DeterministicTest, "Deterministic", null, null, DateTimeOffset.UtcNow);
        var receipts = new ReceiptRepository();
        var receipt = ProviderExecutionReceipt.Start(owner, connection.Id, "marked-pre-dispatch",
            DateTimeOffset.UtcNow, AiProviderKind.GitHubCopilotLocalCli);
        receipt.Complete("""
            {"Succeeded":false,"ErrorCode":"local_copilot_cli_unavailable","Operation":"RuntimeStart","GenerationDispatched":"false"}
            """, DateTimeOffset.UtcNow);
        receipts.Add(receipt);
        var service = CreateService(new ConnectionRepository(connection), receipts, new CountingProvider());

        var result = await service.InspectResumeSafetyAsync(owner,
            AiProviderKind.GitHubCopilotLocalCli, receipt.IdempotencyKey, CancellationToken.None);

        Assert.True(result.Allowed);
        Assert.Equal("Allowed", result.Code);
    }

    [Fact]
    public async Task MissingOwnerScopedReceiptFailsClosed()
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(
            owner, AiProviderKind.DeterministicTest, "Deterministic", null, null, DateTimeOffset.UtcNow);
        var service = CreateService(new ConnectionRepository(connection), new ReceiptRepository(),
            new CountingProvider());

        var result = await service.InspectResumeSafetyAsync(owner,
            AiProviderKind.GitHubCopilotLocalCli, "missing-receipt", CancellationToken.None);

        Assert.False(result.Allowed);
        Assert.Equal("ResumeSafetyUnverifiable", result.Code);
    }

    private static ProviderConnectionService CreateService(
        ConnectionRepository connections,
        ReceiptRepository receipts,
        CountingProvider provider) =>
        new(connections, receipts, new BudgetManager(), new UnitOfWork(), new Protector(), [provider],
            new NeverToolExecutor(), new ProviderMetadataCanonicalizer(
                ProviderCheckpointMetadataContract.CanonicalizeProviderRequestId,
                ProviderCheckpointMetadataContract.CanonicalizeActualModel,
                CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes), TimeProvider.System);

    private sealed class BudgetManager : IProviderBudgetManager
    {
        public Task<BudgetReservationResult> ReserveAsync(AiExecutionRequest request, AiProviderKind provider,
            string modelId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<BudgetReservationResult> ReserveAdditionalRequestAsync(ProviderExecutionReceipt receipt,
            AiExecutionRequest request, DateTimeOffset now, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task ReconcileAsync(ProviderExecutionReceipt receipt, AiExecutionResult result,
            DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class ConnectionRepository(ProviderConnection connection) : IProviderConnectionRepository
    {
        public Task<IReadOnlyCollection<ProviderConnection>> ListAsync(Guid ownerUserId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<ProviderConnection>>(
                connection.OwnerUserId == ownerUserId ? [connection] : []);
        public Task<ProviderConnection?> GetAsync(Guid id, Guid ownerUserId, bool tracking, CancellationToken cancellationToken) =>
            Task.FromResult(connection.Id == id && connection.OwnerUserId == ownerUserId ? connection : null);
        public Task<ProviderConnection?> GetByProviderAsync(Guid ownerUserId, AiProviderKind provider,
            string displayName, CancellationToken cancellationToken) => Task.FromResult<ProviderConnection?>(null);
        public void Add(ProviderConnection item) => throw new NotSupportedException();
    }

    private sealed class ReceiptRepository : IProviderExecutionReceiptRepository
    {
        private readonly List<ProviderExecutionReceipt> items = [];
        public IReadOnlyCollection<ProviderExecutionReceipt> Items => items;
        public Task<ProviderExecutionReceipt?> GetAsync(Guid ownerUserId, string idempotencyKey, CancellationToken cancellationToken) =>
            Task.FromResult(items.SingleOrDefault(item => item.OwnerUserId == ownerUserId
                && item.IdempotencyKey == idempotencyKey));
        public Task<IReadOnlyCollection<ProviderExecutionReceipt>> ListCompletedAsync(
            Guid ownerUserId, DateTimeOffset since, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<ProviderExecutionReceipt>>(items
                .Where(item => item.OwnerUserId == ownerUserId
                    && item.Status == ProviderExecutionReceiptStatus.Completed
                    && item.CompletedAt >= since).ToArray());
        public void Add(ProviderExecutionReceipt receipt) => items.Add(receipt);
    }

    private sealed class UnitOfWork : IIntegrationsUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken cancellationToken) => Task.FromResult(1);
    }
    private sealed class NeverToolExecutor : IToolExecutor
    {
        public Task<ToolInvocationResult> ExecuteAsync(ToolCallRequest call, string idempotencyKey,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class Protector : IProviderCredentialProtector
    {
        public string ProtectCredential(string credential, ProviderCredentialProtectionContext context) => credential;
        public string UnprotectCredential(string protectedCredential, ProviderCredentialProtectionContext context) => protectedCredential;
        public string ProtectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string UnprotectOAuthVerifier(string protectedVerifier, OAuthVerifierProtectionContext context) => protectedVerifier;
        public string Fingerprint(string credential) => "fingerprint";
    }

    private sealed class CountingProvider : IAiProvider
    {
        public AiExecutionResult? Result { get; init; }
        public int CallCount { get; private set; }
        public AiProviderKind Kind => AiProviderKind.DeterministicTest;
        public Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(string? credential, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyCollection<ProviderModelDto>>([
                new("deterministic-v1", "Deterministic", 8192, true, false, 0, 0),
            ]);
        public Task<bool> ValidateAsync(string? credential, CancellationToken cancellationToken) => Task.FromResult(true);
        public Task<AiExecutionResult> ExecuteAsync(
            AiExecutionRequest request,
            string? credential,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(Result ?? new AiExecutionResult(
                true, "result", ProviderFailureKind.None, null, "request-1", 1, 1, 0, []));
        }
    }
}
