using System.Text.Json;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;

namespace AiControlCenter.Integrations.UnitTests;

public sealed class ProviderRouteSecurityTests
{
    [Theory]
    [InlineData(AiProviderKind.OpenAi)]
    [InlineData(AiProviderKind.XAi)]
    public async Task ProviderLabelMismatchIsRejectedBeforeAnyProviderCall(AiProviderKind persistedProvider)
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(owner, persistedProvider, "Personal",
            "ciphertext", "fingerprint", DateTimeOffset.UtcNow);
        var adapter = new CountingProvider(persistedProvider);
        var service = CreateService([connection], adapter);
        var request = new AiExecutionRequest(owner, connection.Id, "known-model", "system", "input",
            [], null, "run:1", MaxInputTokens: 100, MaxOutputTokens: 100,
            ExpectedProvider: AiProviderKind.GitHubCopilot);

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.Configuration, result.FailureKind);
        Assert.Equal("provider_connection_mismatch", result.ErrorCode);
        Assert.Equal(0, adapter.CallCount);
    }

    [Fact]
    public async Task RejectedBudgetReservationPreventsAllPaidProviderCalls()
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(owner, AiProviderKind.OpenAi, "Personal",
            "ciphertext", "fingerprint", DateTimeOffset.UtcNow);
        var adapter = new CountingProvider(AiProviderKind.OpenAi);
        var service = new ProviderConnectionService(new ConnectionRepository([connection]),
            new ReceiptRepository(), new DenyingBudgetManager(), new UnitOfWork(),
            new PassingProtector(), [adapter], new NeverToolExecutor(),
            new ProviderMetadataCanonicalizer(
                ProviderCheckpointMetadataContract.CanonicalizeProviderRequestId,
                ProviderCheckpointMetadataContract.CanonicalizeActualModel,
                CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes), TimeProvider.System);
        var request = new AiExecutionRequest(owner, connection.Id, "gpt-5-mini", "system", "input",
            [], null, "run:budget", DailyCostCapUsd: 1, MonthlyCostCapUsd: 1,
            MaxInputTokens: 1_000_000, MaxOutputTokens: 125_000,
            RunCostCapUsd: 1, RouteCostCapUsd: 1, ExpectedProvider: AiProviderKind.OpenAi,
            MaxToolCycles: 0);

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.LocalBudgetExceeded, result.FailureKind);
        Assert.Equal("local_budget_exceeded", result.ErrorCode);
        Assert.Equal(0, adapter.CallCount);
    }

    [Fact]
    public async Task RequiresReconnectFailsClosedBeforeCatalogOrGenerationCall()
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(owner, AiProviderKind.GitHubCopilotLocalCli,
            "Local Copilot CLI", null, null, DateTimeOffset.UtcNow);
        connection.MarkValidationFailed(ProviderConnectionStatus.RequiresReconnect,
            "Authentication", "Reconnect required.", DateTimeOffset.UtcNow);
        var adapter = new CountingProvider(AiProviderKind.GitHubCopilotLocalCli);
        var service = CreateService([connection], adapter);
        var request = new AiExecutionRequest(owner, connection.Id, "Auto", "system", "input",
            [], null, "run:requires-reconnect", MaxInputTokens: 100, MaxOutputTokens: 100,
            ExpectedProvider: AiProviderKind.GitHubCopilotLocalCli);

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.Authentication, result.FailureKind);
        Assert.Equal("connection_requires_reconnect", result.ErrorCode);
        Assert.Equal(0, adapter.CallCount);
    }

    [Fact]
    public async Task AuthenticationFailureDuringExecutionMarksActiveConnectionForReconnect()
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(owner, AiProviderKind.GitHubCopilotLocalCli,
            "Local Copilot CLI", null, null, DateTimeOffset.UtcNow);
        var adapter = new CountingProvider(AiProviderKind.GitHubCopilotLocalCli)
        {
            Catalog = [new("copilot-model", "Copilot", null, true, false, null, null)],
            ExecutionResult = new(false, null, ProviderFailureKind.Authentication,
                "connection_requires_reconnect", null, 0, 0, null, []),
        };
        var service = CreateService([connection], adapter);
        var request = new AiExecutionRequest(owner, connection.Id, "copilot-model",
            "system", "input", [], null, "run:authentication-expired",
            MaxInputTokens: 100, MaxOutputTokens: 100,
            ExpectedProvider: AiProviderKind.GitHubCopilotLocalCli);

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.Authentication, result.FailureKind);
        Assert.Equal(ProviderConnectionStatus.RequiresReconnect, connection.Status);
        Assert.Equal("Authentication", connection.LastErrorCategory);
        Assert.Equal(1, adapter.CallCount);
    }

    [Fact]
    public async Task GlobalLogicalRouteResolvesToRunOwnersConnectionNotAuthorsConnection()
    {
        var author = Guid.NewGuid();
        var runner = Guid.NewGuid();
        var authorConnection = ProviderConnection.Create(author, AiProviderKind.DeterministicTest,
            "Default", null, null, DateTimeOffset.UtcNow);
        var runnerConnection = ProviderConnection.Create(runner, AiProviderKind.DeterministicTest,
            "Default", null, null, DateTimeOffset.UtcNow);
        var service = CreateService([authorConnection, runnerConnection],
            new CountingProvider(AiProviderKind.DeterministicTest));

        var resolved = await service.ResolveRoutesAsync(runner,
            "[{\"provider\":\"DeterministicTest\",\"connectionName\":\"Default\",\"modelId\":\"deterministic-v1\",\"priority\":1}]",
            CancellationToken.None);

        using var json = JsonDocument.Parse(resolved.Json);
        var route = json.RootElement[0];
        Assert.True(resolved.AllAvailable);
        Assert.Equal(runner, route.GetProperty("ownerUserId").GetGuid());
        Assert.Equal(runnerConnection.Id, route.GetProperty("connectionId").GetGuid());
        Assert.NotEqual(authorConnection.Id, route.GetProperty("connectionId").GetGuid());
    }

    [Fact]
    public async Task ForeignOrMissingConnectionProducesUnavailableBindingWithoutCredentialAccess()
    {
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var connectionA = ProviderConnection.Create(ownerA, AiProviderKind.OpenAi,
            "Private", "ciphertext", "fingerprint", DateTimeOffset.UtcNow);
        var service = CreateService([connectionA], new CountingProvider(AiProviderKind.OpenAi));

        var resolved = await service.ResolveRoutesAsync(ownerB,
            "[{\"provider\":\"OpenAi\",\"connectionName\":\"Private\",\"modelId\":\"gpt-5\",\"priority\":1}]",
            CancellationToken.None);

        using var json = JsonDocument.Parse(resolved.Json);
        Assert.False(resolved.AllAvailable);
        Assert.False(json.RootElement[0].GetProperty("connectionAvailable").GetBoolean());
        Assert.Equal(Guid.Empty, json.RootElement[0].GetProperty("connectionId").GetGuid());
    }

    [Fact]
    public async Task ExplicitLocalCliBindingRequiresTheRunOwnersActiveLocalConnection()
    {
        var owner = Guid.NewGuid();
        var otherOwner = Guid.NewGuid();
        var local = ProviderConnection.Create(owner, AiProviderKind.GitHubCopilotLocalCli,
            "Local Copilot CLI", null, null, DateTimeOffset.UtcNow);
        local.SynchronizeModels("[{\"Id\":\"copilot-auto\",\"DisplayName\":\"Copilot Auto\"}]",
            DateTimeOffset.UtcNow);
        var service = CreateService([local],
            new CountingProvider(AiProviderKind.GitHubCopilotLocalCli));

        var owned = await service.ResolveRoutesAsync(owner,
            $$"""[{"provider":"LocalCopilotCli","connectionId":"{{local.Id}}","modelId":"Auto","priority":1,"paidExecutionAllowed":false,"paidFallbackAllowed":false}]""",
            CancellationToken.None);
        var forged = await service.ResolveRoutesAsync(otherOwner,
            $$"""[{"provider":"LocalCopilotCli","connectionId":"{{local.Id}}","modelId":"Auto","priority":1,"paidExecutionAllowed":false,"paidFallbackAllowed":false}]""",
            CancellationToken.None);

        using var ownedJson = JsonDocument.Parse(owned.Json);
        using var forgedJson = JsonDocument.Parse(forged.Json);
        Assert.True(owned.AllAvailable);
        Assert.Equal(local.Id, ownedJson.RootElement[0].GetProperty("connectionId").GetGuid());
        Assert.Equal("GitHubCopilotLocalCli", ownedJson.RootElement[0].GetProperty("provider").GetString());
        Assert.False(forged.AllAvailable);
        Assert.Equal(Guid.Empty, forgedJson.RootElement[0].GetProperty("connectionId").GetGuid());
    }

    [Fact]
    public async Task LocalCliRoutePreflightFailsClosedWhenTheLiveRuntimeIsUnavailable()
    {
        var owner = Guid.NewGuid();
        var local = ProviderConnection.Create(owner, AiProviderKind.GitHubCopilotLocalCli,
            "Local Copilot CLI", null, null, DateTimeOffset.UtcNow);
        local.SynchronizeModels("[{\"Id\":\"auto\",\"DisplayName\":\"Auto\"}]",
            DateTimeOffset.UtcNow);
        var adapter = new CountingProvider(AiProviderKind.GitHubCopilotLocalCli)
        {
            ValidationKind = ProviderCredentialValidationKind.TemporarilyUnavailable,
        };

        var resolved = await CreateService([local], adapter).ResolveRoutesAsync(owner,
            $$"""[{"provider":"LocalCopilotCli","connectionId":"{{local.Id}}","modelId":"Auto","priority":1}]""",
            CancellationToken.None);

        using var json = JsonDocument.Parse(resolved.Json);
        Assert.False(resolved.AllAvailable);
        Assert.False(json.RootElement[0].GetProperty("connectionAvailable").GetBoolean());
        Assert.Equal(Guid.Empty, json.RootElement[0].GetProperty("connectionId").GetGuid());
    }

    [Theory]
    [InlineData(ProviderCredentialValidationKind.RateLimited, "RateLimited")]
    [InlineData(ProviderCredentialValidationKind.TemporarilyUnavailable, "TemporarilyUnavailable")]
    [InlineData(ProviderCredentialValidationKind.ModelCatalogUnavailable, "ModelCatalogUnavailable")]
    [InlineData(ProviderCredentialValidationKind.UnknownFailure, "Unknown")]
    public async Task TemporaryValidationFailureDegradesWithoutDeletingCredential(
        ProviderCredentialValidationKind kind,
        string category)
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(owner, AiProviderKind.GitHubCopilot, "Copilot",
            "current-user-oauth-token", "fingerprint", DateTimeOffset.UtcNow);
        var provider = new CountingProvider(AiProviderKind.GitHubCopilot) { ValidationKind = kind };

        var result = await CreateValidationService([connection], provider)
            .ValidateAsync(connection.Id, owner, CancellationToken.None);

        Assert.Equal(ProviderConnectionStatus.Degraded, result.Status);
        Assert.Equal(category, result.LastErrorCategory);
        Assert.Equal("current-user-oauth-token", provider.LastCredential);
        Assert.NotNull(connection.ProtectedCredential);
    }

    [Theory]
    [InlineData(ProviderCredentialValidationKind.InvalidCredential, ProviderConnectionStatus.Invalid, "Authentication")]
    [InlineData(ProviderCredentialValidationKind.RequiresReconnect, ProviderConnectionStatus.RequiresReconnect, "Authentication")]
    [InlineData(ProviderCredentialValidationKind.NotEntitled, ProviderConnectionStatus.NotEntitled, "Entitlement")]
    [InlineData(ProviderCredentialValidationKind.PolicyBlocked, ProviderConnectionStatus.PolicyBlocked, "PolicyBlocked")]
    [InlineData(ProviderCredentialValidationKind.OAuthAppUnsupported, ProviderConnectionStatus.OAuthAppUnsupported, "OAuthAppUnsupported")]
    [InlineData(ProviderCredentialValidationKind.InsufficientPermissions, ProviderConnectionStatus.InsufficientPermissions, "InsufficientPermissions")]
    [InlineData(ProviderCredentialValidationKind.QuotaExhausted, ProviderConnectionStatus.QuotaExhausted, "QuotaExhausted")]
    public async Task PermanentValidationFailureHasSpecificClassification(
        ProviderCredentialValidationKind kind,
        ProviderConnectionStatus status,
        string category)
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(owner, AiProviderKind.GitHubCopilot, "Copilot",
            "current-user-oauth-token", "fingerprint", DateTimeOffset.UtcNow);
        var provider = new CountingProvider(AiProviderKind.GitHubCopilot) { ValidationKind = kind };

        var result = await CreateValidationService([connection], provider)
            .ValidateAsync(connection.Id, owner, CancellationToken.None);

        Assert.Equal(status, result.Status);
        Assert.Equal(category, result.LastErrorCategory);
        Assert.NotNull(connection.ProtectedCredential);
    }

    [Fact]
    public async Task SuccessfulValidationActivatesConnectionAndSynchronizesCatalog()
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(owner, AiProviderKind.GitHubCopilot, "Copilot",
            "current-user-oauth-token", "fingerprint", DateTimeOffset.UtcNow);
        connection.MarkValidated(false, DateTimeOffset.UtcNow);
        var provider = new CountingProvider(AiProviderKind.GitHubCopilot)
        {
            ValidationKind = ProviderCredentialValidationKind.Valid,
            ValidationModels = [new("copilot-model", "Copilot", null, true, true, null, null)],
        };

        var result = await CreateValidationService([connection], provider)
            .ValidateAsync(connection.Id, owner, CancellationToken.None);

        Assert.Equal(ProviderConnectionStatus.Active, result.Status);
        Assert.NotNull(result.LastSyncedAt);
        Assert.Contains("copilot-model", connection.AvailableModelsJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidationDecryptsTheCurrentCredentialVersionForTheOwningConnection()
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(owner, AiProviderKind.GitHubCopilot, "Copilot",
            "protected-v1", "fingerprint-v1", DateTimeOffset.UtcNow);
        connection.ReplaceCredential("protected-v2", "fingerprint-v2", DateTimeOffset.UtcNow);
        var protector = new VersionTrackingProtector();
        var provider = new CountingProvider(AiProviderKind.GitHubCopilot);
        var originalFingerprint = connection.CredentialFingerprint;
        var originalCredentialVersion = connection.CredentialVersion;

        await CreateValidationService([connection], provider, protector)
            .ValidateAsync(connection.Id, owner, CancellationToken.None);

        Assert.Equal(2, protector.Context?.CredentialVersion);
        Assert.Equal(owner, protector.Context?.OwnerUserId);
        Assert.Equal(connection.Id, protector.Context?.ConnectionId);
        Assert.Equal("credential-v2", provider.LastCredential);
        Assert.Equal(originalFingerprint, connection.CredentialFingerprint);
        Assert.Equal(originalCredentialVersion, connection.CredentialVersion);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("[{\"provider\":\"DeterministicTest\",\"modelId\":\"deterministic-v1\",\"priority\":1,\"connectionId\":\"not-a-guid\"}]")]
    [InlineData("[{\"provider\":\"UnknownProvider\",\"modelId\":\"model\",\"priority\":1}]")]
    [InlineData("[{\"modelId\":\"deterministic-v1\",\"priority\":1}]")]
    [InlineData("[{\"provider\":\"DeterministicTest\",\"priority\":1}]")]
    [InlineData("[{\"provider\":1,\"modelId\":\"deterministic-v1\",\"priority\":1}]")]
    [InlineData("[{\"provider\":\"DeterministicTest\",\"modelId\":1,\"priority\":1}]")]
    [InlineData("[{\"provider\":\"DeterministicTest\",\"modelId\":\"deterministic-v1\",\"priority\":\"first\"}]")]
    public async Task MalformedPersistedLogicalRouteFailsClosedBeforeProviderOrCredentialAccess(string routes)
    {
        var adapter = new CountingProvider(AiProviderKind.DeterministicTest);
        var service = CreateService([], adapter);

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            service.ResolveRoutesAsync(Guid.NewGuid(), routes, CancellationToken.None));

        Assert.Equal("logicalRoutesJson", exception.ParamName);
        Assert.DoesNotContain(routes, exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, adapter.CallCount);
    }

    private static ProviderConnectionService CreateService(
        IReadOnlyCollection<ProviderConnection> connections,
        IAiProvider provider) => new(new ConnectionRepository(connections), new ReceiptRepository(),
            new BudgetManager(), new UnitOfWork(), new Protector(), [provider],
            new NeverToolExecutor(), new ProviderMetadataCanonicalizer(
                ProviderCheckpointMetadataContract.CanonicalizeProviderRequestId,
                ProviderCheckpointMetadataContract.CanonicalizeActualModel,
                CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes), TimeProvider.System);

    private static ProviderConnectionService CreateValidationService(
        IReadOnlyCollection<ProviderConnection> connections,
        IAiProvider provider,
        IProviderCredentialProtector? protector = null) => new(new ConnectionRepository(connections), new ReceiptRepository(),
            new BudgetManager(), new UnitOfWork(), protector ?? new PassingProtector(), [provider],
            new NeverToolExecutor(), new ProviderMetadataCanonicalizer(
                ProviderCheckpointMetadataContract.CanonicalizeProviderRequestId,
                ProviderCheckpointMetadataContract.CanonicalizeActualModel,
                CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes), TimeProvider.System);

    private sealed class ConnectionRepository(IReadOnlyCollection<ProviderConnection> items)
        : IProviderConnectionRepository
    {
        public Task<IReadOnlyCollection<ProviderConnection>> ListAsync(Guid ownerUserId, CancellationToken token) =>
            Task.FromResult<IReadOnlyCollection<ProviderConnection>>(items.Where(x => x.OwnerUserId == ownerUserId).ToArray());
        public Task<ProviderConnection?> GetAsync(Guid id, Guid ownerUserId, bool tracking, CancellationToken token) =>
            Task.FromResult(items.SingleOrDefault(x => x.Id == id && x.OwnerUserId == ownerUserId));
        public Task<ProviderConnection?> GetByProviderAsync(Guid ownerUserId, AiProviderKind provider,
            string displayName, CancellationToken token) => Task.FromResult(items.SingleOrDefault(x =>
                x.OwnerUserId == ownerUserId && x.Provider == provider && x.DisplayName == displayName));
        public void Add(ProviderConnection connection) => throw new NotSupportedException();
    }
    private sealed class ReceiptRepository : IProviderExecutionReceiptRepository
    {
        public Task<ProviderExecutionReceipt?> GetAsync(Guid ownerUserId, string key, CancellationToken token) =>
            Task.FromResult<ProviderExecutionReceipt?>(null);
        public Task<IReadOnlyCollection<ProviderExecutionReceipt>> ListCompletedAsync(Guid ownerUserId,
            DateTimeOffset since, CancellationToken token) => Task.FromResult<IReadOnlyCollection<ProviderExecutionReceipt>>([]);
        public void Add(ProviderExecutionReceipt receipt) { }
    }
    private sealed class BudgetManager : IProviderBudgetManager
    {
        public Task<BudgetReservationResult> ReserveAsync(AiExecutionRequest request, AiProviderKind provider,
            string modelId, DateTimeOffset now, CancellationToken token) =>
            throw new Xunit.Sdk.XunitException("Budget reservation must not run for a provider mismatch.");
        public Task<BudgetReservationResult> ReserveAdditionalRequestAsync(ProviderExecutionReceipt receipt,
            AiExecutionRequest request, DateTimeOffset now, CancellationToken token) =>
            throw new Xunit.Sdk.XunitException("Additional reservation must not run for a provider mismatch.");
        public Task ReconcileAsync(ProviderExecutionReceipt receipt, AiExecutionResult result,
            DateTimeOffset now, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class DenyingBudgetManager : IProviderBudgetManager
    {
        public Task<BudgetReservationResult> ReserveAsync(AiExecutionRequest request, AiProviderKind provider,
            string modelId, DateTimeOffset now, CancellationToken token) =>
            Task.FromResult(new BudgetReservationResult(false, null, "local_budget_exceeded"));
        public Task<BudgetReservationResult> ReserveAdditionalRequestAsync(ProviderExecutionReceipt receipt,
            AiExecutionRequest request, DateTimeOffset now, CancellationToken token) =>
            Task.FromResult(new BudgetReservationResult(false, receipt, "local_budget_exceeded"));
        public Task ReconcileAsync(ProviderExecutionReceipt receipt, AiExecutionResult result,
            DateTimeOffset now, CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class UnitOfWork : IIntegrationsUnitOfWork
    { public Task<int> SaveChangesAsync(CancellationToken token) => Task.FromResult(1); }
    private sealed class NeverToolExecutor : IToolExecutor
    {
        public Task<ToolInvocationResult> ExecuteAsync(ToolCallRequest call, string key,
            CancellationToken token) => throw new NotSupportedException();
    }
    private sealed class Protector : IProviderCredentialProtector
    {
        public string ProtectCredential(string credential, ProviderCredentialProtectionContext context) => credential;
        public string UnprotectCredential(string protectedCredential, ProviderCredentialProtectionContext context) =>
            throw new Xunit.Sdk.XunitException("A foreign credential must not be decrypted.");
        public string ProtectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string UnprotectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string Fingerprint(string credential) => "fingerprint";
    }
    private sealed class PassingProtector : IProviderCredentialProtector
    {
        public string ProtectCredential(string credential, ProviderCredentialProtectionContext context) => credential;
        public string UnprotectCredential(string protectedCredential, ProviderCredentialProtectionContext context) =>
            protectedCredential;
        public string ProtectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string UnprotectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string Fingerprint(string credential) => "fingerprint";
    }
    private sealed class VersionTrackingProtector : IProviderCredentialProtector
    {
        public ProviderCredentialProtectionContext? Context { get; private set; }
        public string ProtectCredential(string credential, ProviderCredentialProtectionContext context) => credential;
        public string UnprotectCredential(string protectedCredential, ProviderCredentialProtectionContext context)
        {
            Context = context;
            return $"credential-v{context.CredentialVersion}";
        }
        public string ProtectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string UnprotectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string Fingerprint(string credential) => "fingerprint";
    }
    private sealed class CountingProvider(AiProviderKind kind) : IAiProvider
    {
        public int CallCount { get; private set; }
        public string? LastCredential { get; private set; }
        public ProviderCredentialValidationKind ValidationKind { get; init; } = ProviderCredentialValidationKind.Valid;
        public IReadOnlyCollection<ProviderModelDto>? ValidationModels { get; init; }
        public IReadOnlyCollection<ProviderModelDto> Catalog { get; init; } = [];
        public AiExecutionResult ExecutionResult { get; init; } = new(
            true, "ok", ProviderFailureKind.None, null, null, 1, 1, 0, []);
        public AiProviderKind Kind => kind;
        public Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(string? credential, CancellationToken token)
        { CallCount++; return Task.FromResult(Catalog); }
        public Task<bool> ValidateAsync(string? credential, CancellationToken token) => Task.FromResult(true);
        public Task<ProviderCredentialValidationResult> ValidateConnectionAsync(
            string? credential,
            CancellationToken token)
        {
            LastCredential = credential;
            return Task.FromResult(new ProviderCredentialValidationResult(ValidationKind, ValidationModels));
        }
        public Task<AiExecutionResult> ExecuteAsync(AiExecutionRequest request, string? credential, CancellationToken token)
        {
            CallCount++;
            return Task.FromResult(ExecutionResult);
        }
    }
}
