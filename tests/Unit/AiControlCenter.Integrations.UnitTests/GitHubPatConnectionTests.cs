using System.Text.Json;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;

namespace AiControlCenter.Integrations.UnitTests;

public sealed class GitHubPatConnectionTests
{
    private const string FineGrainedPat = "github_pat_test_value_1234567890";

    [Fact]
    public async Task ValidFineGrainedPatIsValidatedBeforeItIsEncryptedAndStored()
    {
        var owner = Guid.NewGuid();
        var repository = new ConnectionRepository();
        var protector = new TrackingProtector();
        var unitOfWork = new UnitOfWork();
        var provider = ValidProvider("12345", "expected-user");
        var service = CreateService(repository, protector, unitOfWork, provider);

        var result = await service.ConnectGitHubPatAsync(
            owner, FineGrainedPat, "expected-user", CancellationToken.None);

        Assert.Equal(FineGrainedPat, provider.LastCredential);
        Assert.Equal(FineGrainedPat, protector.LastProtectedCredential);
        Assert.Equal(owner, protector.LastProtectionContext?.OwnerUserId);
        Assert.Equal(ProviderConnectionStatus.Active, result.Status);
        Assert.Equal("12345", result.ExternalAccountId);
        Assert.Equal("expected-user", result.ExternalAccountLogin);
        Assert.NotNull(result.LastValidatedAt);
        Assert.NotNull(result.LastSyncedAt);
        Assert.Equal("sha256:testfingerprint", result.CredentialFingerprint);
        Assert.Single(repository.Items);
        Assert.Equal(1, unitOfWork.SaveCount);
        Assert.DoesNotContain(FineGrainedPat, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ghp_classic_pat_is_not_supported")]
    [InlineData("gho_oauth_tokens_use_the_oauth_flow")]
    [InlineData("plain-secret")]
    [InlineData("github_pat_fictional internal-whitespace")]
    public async Task UnsupportedGitHubCredentialTypeIsRejectedBeforeProviderOrStorage(string credential)
    {
        var repository = new ConnectionRepository();
        var protector = new TrackingProtector();
        var unitOfWork = new UnitOfWork();
        var provider = ValidProvider("12345", "expected-user");
        var service = CreateService(repository, protector, unitOfWork, provider);

        var error = await Assert.ThrowsAsync<ProviderCredentialRejectedException>(() =>
            service.ConnectGitHubPatAsync(Guid.NewGuid(), credential, "expected-user", CancellationToken.None));

        Assert.Equal("UnsupportedGitHubCredentialType", error.Code);
        Assert.Equal(0, provider.ValidationCallCount);
        Assert.Null(protector.LastProtectedCredential);
        Assert.Empty(repository.Items);
        Assert.Equal(0, unitOfWork.SaveCount);
        Assert.DoesNotContain(credential, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrefixWithoutOpaqueValueIsRejectedBeforeProviderOrStorage()
    {
        var repository = new ConnectionRepository();
        var protector = new TrackingProtector();
        var unitOfWork = new UnitOfWork();
        var provider = ValidProvider("12345", "expected-user");
        var service = CreateService(repository, protector, unitOfWork, provider);

        var error = await Assert.ThrowsAsync<ProviderCredentialRejectedException>(() =>
            service.ConnectGitHubPatAsync(
                Guid.NewGuid(), "github_pat_", "expected-user", CancellationToken.None));

        Assert.Equal("UnsupportedGitHubCredentialType", error.Code);
        Assert.Equal(0, provider.ValidationCallCount);
        Assert.Null(protector.LastProtectedCredential);
        Assert.Empty(repository.Items);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task HyphenatedLoginAndSurroundingWhitespaceAreNormalizedBeforeProviderValidation()
    {
        var repository = new ConnectionRepository();
        var protector = new TrackingProtector();
        var unitOfWork = new UnitOfWork();
        var provider = ValidProvider("12345", "student-user");
        var service = CreateService(repository, protector, unitOfWork, provider);
        var opaquePat = $"github_pat_{new string('A', 180)}";

        var result = await service.ConnectGitHubPatAsync(
            Guid.NewGuid(), $" \r\n{opaquePat}\r\n ", "  @student-user  ", CancellationToken.None);

        Assert.Equal(opaquePat, provider.LastCredential);
        Assert.Equal("student-user", result.ExternalAccountLogin);
        Assert.Single(repository.Items);
        Assert.Equal(1, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task RepositoryPermissionMetadataIsNotRequiredBeforeProviderValidation()
    {
        var repository = new ConnectionRepository();
        var protector = new TrackingProtector();
        var unitOfWork = new UnitOfWork();
        var provider = ValidProvider("12345", "student-user");
        var service = CreateService(repository, protector, unitOfWork, provider);

        _ = await service.ConnectGitHubPatAsync(
            Guid.NewGuid(), FineGrainedPat, "student-user", CancellationToken.None);

        Assert.Equal(1, provider.ValidationCallCount);
        Assert.Equal(FineGrainedPat, provider.LastCredential);
        Assert.Single(repository.Items);
    }

    [Fact]
    public async Task FailedReplacementDoesNotIncrementVersionOrPersistPartialCredential()
    {
        var owner = Guid.NewGuid();
        var existing = ProviderConnection.Create(owner, AiProviderKind.GitHubCopilot,
            "GitHub Copilot", "encrypted-existing", "sha256:existing", DateTimeOffset.UtcNow);
        existing.BindExternalAccount("12345", "student-user", DateTimeOffset.UtcNow);
        var repository = new ConnectionRepository(existing);
        var protector = new TrackingProtector();
        var unitOfWork = new UnitOfWork();
        var provider = new StubProvider(new(ProviderCredentialValidationKind.RateLimited,
            ExternalAccountId: "12345", ExternalAccountLogin: "student-user"));
        var service = CreateService(repository, protector, unitOfWork, provider);

        _ = await Assert.ThrowsAsync<ProviderCredentialRejectedException>(() =>
            service.ConnectGitHubPatAsync(owner, $"  {FineGrainedPat}  ",
                "student-user", CancellationToken.None));

        Assert.Equal("encrypted-existing", existing.ProtectedCredential);
        Assert.Equal("sha256:existing", existing.CredentialFingerprint);
        Assert.Equal(1, existing.CredentialVersion);
        Assert.Null(protector.LastProtectedCredential);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task GenericManualCreateCannotBypassGitHubCredentialValidation()
    {
        var repository = new ConnectionRepository();
        var protector = new TrackingProtector();
        var unitOfWork = new UnitOfWork();
        var provider = ValidProvider("12345", "expected-user");
        var service = CreateService(repository, protector, unitOfWork, provider);

        var error = await Assert.ThrowsAsync<ProviderCredentialRejectedException>(() =>
            service.CreateManualAsync(Guid.NewGuid(), AiProviderKind.GitHubCopilot,
                "Bypass", "ghp_classic-pat", CancellationToken.None));

        Assert.Equal("GitHubConnectionFlowRequired", error.Code);
        Assert.Equal(0, provider.ValidationCallCount);
        Assert.Null(protector.LastProtectedCredential);
        Assert.Empty(repository.Items);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task GenericCredentialReplacementCannotBypassGitHubCredentialValidation()
    {
        var owner = Guid.NewGuid();
        var existing = ProviderConnection.Create(owner, AiProviderKind.GitHubCopilot,
            "GitHub Copilot", "encrypted-oauth", "sha256:old", DateTimeOffset.UtcNow);
        var repository = new ConnectionRepository(existing);
        var protector = new TrackingProtector();
        var unitOfWork = new UnitOfWork();
        var service = CreateService(repository, protector, unitOfWork,
            ValidProvider("12345", "expected-user"));

        var error = await Assert.ThrowsAsync<ProviderCredentialRejectedException>(() =>
            service.ReplaceCredentialAsync(existing.Id, owner,
                "ghp_classic-pat", CancellationToken.None));

        Assert.Equal("GitHubConnectionFlowRequired", error.Code);
        Assert.Equal("encrypted-oauth", existing.ProtectedCredential);
        Assert.Equal(1, existing.CredentialVersion);
        Assert.Null(protector.LastProtectedCredential);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task UnexpectedGitHubLoginIsRejectedWithoutPersistingPat()
    {
        var repository = new ConnectionRepository();
        var protector = new TrackingProtector();
        var unitOfWork = new UnitOfWork();
        var service = CreateService(repository, protector, unitOfWork,
            ValidProvider("12345", "different-user"));

        var error = await Assert.ThrowsAsync<ProviderCredentialRejectedException>(() =>
            service.ConnectGitHubPatAsync(
                Guid.NewGuid(), FineGrainedPat, "expected-user", CancellationToken.None));

        Assert.Equal("GitHubAccountMismatch", error.Code);
        Assert.Null(protector.LastProtectedCredential);
        Assert.Empty(repository.Items);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task ReplacementCannotSwitchTheExistingConnectionToAnotherGitHubNumericIdentity()
    {
        var owner = Guid.NewGuid();
        var existing = ProviderConnection.Create(owner, AiProviderKind.GitHubCopilot,
            "GitHub Copilot", "encrypted-oauth", "sha256:old", DateTimeOffset.UtcNow);
        existing.BindExternalAccount("111", "expected-user", DateTimeOffset.UtcNow);
        var repository = new ConnectionRepository(existing);
        var protector = new TrackingProtector();
        var unitOfWork = new UnitOfWork();
        var service = CreateService(repository, protector, unitOfWork,
            ValidProvider("222", "expected-user"));

        var error = await Assert.ThrowsAsync<ProviderCredentialRejectedException>(() =>
            service.ConnectGitHubPatAsync(owner, FineGrainedPat, "expected-user", CancellationToken.None));

        Assert.Equal("GitHubAccountMismatch", error.Code);
        Assert.Equal("encrypted-oauth", existing.ProtectedCredential);
        Assert.Equal("sha256:old", existing.CredentialFingerprint);
        Assert.Equal(1, existing.CredentialVersion);
        Assert.Null(protector.LastProtectedCredential);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task OwnersReceiveSeparateConnectionsAndNeverReplaceAnotherUsersCredential()
    {
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        var foreign = ProviderConnection.Create(ownerA, AiProviderKind.GitHubCopilot,
            "GitHub Copilot", "owner-a-encrypted", "sha256:owner-a", DateTimeOffset.UtcNow);
        foreign.BindExternalAccount("111", "owner-a", DateTimeOffset.UtcNow);
        var repository = new ConnectionRepository(foreign);
        var protector = new TrackingProtector();
        var unitOfWork = new UnitOfWork();
        var service = CreateService(repository, protector, unitOfWork,
            ValidProvider("222", "owner-b"));

        var result = await service.ConnectGitHubPatAsync(
            ownerB, FineGrainedPat, "owner-b", CancellationToken.None);

        Assert.Equal(2, repository.Items.Count);
        Assert.Equal("owner-a-encrypted", foreign.ProtectedCredential);
        Assert.Equal(ownerB, protector.LastProtectionContext?.OwnerUserId);
        Assert.Equal("222", result.ExternalAccountId);
    }

    [Theory]
    [InlineData(ProviderCredentialValidationKind.InvalidCredential, "InvalidCredential")]
    [InlineData(ProviderCredentialValidationKind.NotEntitled, "NotEntitled")]
    [InlineData(ProviderCredentialValidationKind.PolicyBlocked, "PolicyBlocked")]
    [InlineData(ProviderCredentialValidationKind.OAuthAppUnsupported, "OAuthAppUnsupported")]
    [InlineData(ProviderCredentialValidationKind.InsufficientPermissions, "InsufficientPermissions")]
    [InlineData(ProviderCredentialValidationKind.QuotaExhausted, "QuotaExhausted")]
    [InlineData(ProviderCredentialValidationKind.RateLimited, "RateLimited")]
    [InlineData(ProviderCredentialValidationKind.TemporarilyUnavailable, "TemporarilyUnavailable")]
    [InlineData(ProviderCredentialValidationKind.ModelCatalogUnavailable, "GitHubModelCatalogUnavailable")]
    public async Task ProviderValidationFailureIsTypedAndDoesNotStorePat(
        ProviderCredentialValidationKind kind,
        string expectedCode)
    {
        var repository = new ConnectionRepository();
        var protector = new TrackingProtector();
        var unitOfWork = new UnitOfWork();
        var provider = new StubProvider(new(kind,
            ExternalAccountId: "12345", ExternalAccountLogin: "expected-user"));
        var service = CreateService(repository, protector, unitOfWork, provider);

        var error = await Assert.ThrowsAsync<ProviderCredentialRejectedException>(() =>
            service.ConnectGitHubPatAsync(
                Guid.NewGuid(), FineGrainedPat, "expected-user", CancellationToken.None));

        Assert.Equal(expectedCode, error.Code);
        Assert.Null(protector.LastProtectedCredential);
        Assert.Empty(repository.Items);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    [Fact]
    public async Task EmptyModelCatalogIsRejectedBeforePatIsStored()
    {
        var repository = new ConnectionRepository();
        var protector = new TrackingProtector();
        var unitOfWork = new UnitOfWork();
        var provider = new StubProvider(new(ProviderCredentialValidationKind.Valid, [],
            "12345", "expected-user"));
        var service = CreateService(repository, protector, unitOfWork, provider);

        var error = await Assert.ThrowsAsync<ProviderCredentialRejectedException>(() =>
            service.ConnectGitHubPatAsync(
                Guid.NewGuid(), FineGrainedPat, "expected-user", CancellationToken.None));

        Assert.Equal("GitHubModelCatalogUnavailable", error.Code);
        Assert.Null(protector.LastProtectedCredential);
        Assert.Empty(repository.Items);
        Assert.Equal(0, unitOfWork.SaveCount);
    }

    private static StubProvider ValidProvider(string id, string login) => new(new(
        ProviderCredentialValidationKind.Valid,
        [new("copilot-model", "Copilot Model", null, true, true, null, null)],
        id,
        login));

    private static ProviderConnectionService CreateService(
        ConnectionRepository repository,
        TrackingProtector protector,
        UnitOfWork unitOfWork,
        StubProvider provider) => new(repository, new ReceiptRepository(), new BudgetManager(), unitOfWork,
            protector, [provider], new NeverToolExecutor(), new ProviderMetadataCanonicalizer(
                ProviderCheckpointMetadataContract.CanonicalizeProviderRequestId,
                ProviderCheckpointMetadataContract.CanonicalizeActualModel,
                CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes), TimeProvider.System);

    private sealed class ConnectionRepository(params ProviderConnection[] connections)
        : IProviderConnectionRepository
    {
        public List<ProviderConnection> Items { get; } = [.. connections];
        public Task<IReadOnlyCollection<ProviderConnection>> ListAsync(Guid owner, CancellationToken token) =>
            Task.FromResult<IReadOnlyCollection<ProviderConnection>>(
                Items.Where(connection => connection.OwnerUserId == owner).ToArray());
        public Task<ProviderConnection?> GetAsync(Guid id, Guid owner, bool tracking, CancellationToken token) =>
            Task.FromResult(Items.SingleOrDefault(connection =>
                connection.Id == id && connection.OwnerUserId == owner));
        public Task<ProviderConnection?> GetByProviderAsync(
            Guid owner, AiProviderKind provider, string name, CancellationToken token) =>
            Task.FromResult(Items.SingleOrDefault(connection => connection.OwnerUserId == owner
                && connection.Provider == provider && connection.DisplayName == name));
        public void Add(ProviderConnection connection) => Items.Add(connection);
    }

    private sealed class TrackingProtector : IProviderCredentialProtector
    {
        public string? LastProtectedCredential { get; private set; }
        public ProviderCredentialProtectionContext? LastProtectionContext { get; private set; }
        public string ProtectCredential(string credential, ProviderCredentialProtectionContext context)
        {
            LastProtectedCredential = credential;
            LastProtectionContext = context;
            return "encrypted-pat";
        }
        public string UnprotectCredential(string protectedCredential, ProviderCredentialProtectionContext context) =>
            throw new NotSupportedException();
        public string ProtectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string UnprotectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string Fingerprint(string credential) => "sha256:testfingerprint";
    }

    private sealed class StubProvider(ProviderCredentialValidationResult validation) : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.GitHubCopilot;
        public int ValidationCallCount { get; private set; }
        public string? LastCredential { get; private set; }
        public Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(
            string? credential, CancellationToken token) =>
            Task.FromResult<IReadOnlyCollection<ProviderModelDto>>(validation.Models ?? []);
        public Task<bool> ValidateAsync(string? credential, CancellationToken token) => Task.FromResult(true);
        public Task<ProviderCredentialValidationResult> ValidateConnectionAsync(
            string? credential, CancellationToken token)
        {
            ValidationCallCount++;
            LastCredential = credential;
            return Task.FromResult(validation);
        }
        public Task<AiExecutionResult> ExecuteAsync(
            AiExecutionRequest request, string? credential, CancellationToken token) =>
            throw new NotSupportedException();
    }

    private sealed class UnitOfWork : IIntegrationsUnitOfWork
    {
        public int SaveCount { get; private set; }
        public Task<int> SaveChangesAsync(CancellationToken token)
        {
            SaveCount++;
            return Task.FromResult(1);
        }
    }

    private sealed class ReceiptRepository : IProviderExecutionReceiptRepository
    {
        public Task<ProviderExecutionReceipt?> GetAsync(Guid owner, string key, CancellationToken token) =>
            Task.FromResult<ProviderExecutionReceipt?>(null);
        public Task<IReadOnlyCollection<ProviderExecutionReceipt>> ListCompletedAsync(
            Guid owner, DateTimeOffset since, CancellationToken token) =>
            Task.FromResult<IReadOnlyCollection<ProviderExecutionReceipt>>([]);
        public void Add(ProviderExecutionReceipt receipt) => throw new NotSupportedException();
    }

    private sealed class BudgetManager : IProviderBudgetManager
    {
        public Task<BudgetReservationResult> ReserveAsync(AiExecutionRequest request, AiProviderKind provider,
            string modelId, DateTimeOffset now, CancellationToken token) => throw new NotSupportedException();
        public Task<BudgetReservationResult> ReserveAdditionalRequestAsync(ProviderExecutionReceipt receipt,
            AiExecutionRequest request, DateTimeOffset now, CancellationToken token) => throw new NotSupportedException();
        public Task ReconcileAsync(ProviderExecutionReceipt receipt, AiExecutionResult result,
            DateTimeOffset now, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class NeverToolExecutor : IToolExecutor
    {
        public Task<ToolInvocationResult> ExecuteAsync(
            ToolCallRequest request, string idempotencyKey, CancellationToken token) =>
            throw new NotSupportedException();
    }
}
