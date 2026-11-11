using AiControlCenter.Integrations.Domain;

namespace AiControlCenter.Integrations.Application;

public interface IProviderConnectionRepository
{
    Task<IReadOnlyCollection<ProviderConnection>> ListAsync(Guid ownerUserId, CancellationToken cancellationToken);
    Task<ProviderConnection?> GetAsync(Guid id, Guid ownerUserId, bool tracking, CancellationToken cancellationToken);
    Task<ProviderConnection?> GetByProviderAsync(
        Guid ownerUserId, AiProviderKind provider, string displayName, CancellationToken cancellationToken);
    void Add(ProviderConnection connection);
}
public interface IProviderOAuthStateRepository
{
    Task<ProviderOAuthState?> GetByHashAsync(string stateHash, CancellationToken cancellationToken);
    void Add(ProviderOAuthState state);
}
public interface IProviderExecutionReceiptRepository
{
    Task<ProviderExecutionReceipt?> GetAsync(Guid ownerUserId, string idempotencyKey, CancellationToken cancellationToken);
    Task<IReadOnlyCollection<ProviderExecutionReceipt>> ListCompletedAsync(
        Guid ownerUserId, DateTimeOffset since, CancellationToken cancellationToken);
    void Add(ProviderExecutionReceipt receipt);
}
public sealed record BudgetReservationResult(
    bool Allowed,
    ProviderExecutionReceipt? Receipt,
    string? ErrorCode);
public interface IProviderBudgetManager
{
    Task<BudgetReservationResult> ReserveAsync(
        AiExecutionRequest request,
        AiProviderKind provider,
        string modelId,
        DateTimeOffset now,
        CancellationToken cancellationToken);
    Task<BudgetReservationResult> ReserveAdditionalRequestAsync(
        ProviderExecutionReceipt receipt,
        AiExecutionRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);
    Task ReconcileAsync(
        ProviderExecutionReceipt receipt,
        AiExecutionResult result,
        DateTimeOffset now,
        CancellationToken cancellationToken);
}
public interface IIntegrationsUnitOfWork { Task<int> SaveChangesAsync(CancellationToken cancellationToken); }
public interface IProviderCredentialProtector
{
    string ProtectCredential(string credential, ProviderCredentialProtectionContext context);
    string UnprotectCredential(string protectedCredential, ProviderCredentialProtectionContext context);
    string ProtectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context);
    string UnprotectOAuthVerifier(string protectedVerifier, OAuthVerifierProtectionContext context);
    string Fingerprint(string credential);
}
public sealed record ProviderCredentialProtectionContext(
    AiProviderKind Provider, Guid OwnerUserId, Guid ConnectionId, int CredentialVersion);
public sealed record OAuthVerifierProtectionContext(
    AiProviderKind Provider, Guid OwnerUserId, Guid FlowId);
public interface IAiProvider
{
    AiProviderKind Kind { get; }
    bool IsAvailableForRouting => true;
    Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(string? credential, CancellationToken cancellationToken);
    Task<bool> ValidateAsync(string? credential, CancellationToken cancellationToken);
    async Task<ProviderCredentialValidationResult> ValidateConnectionAsync(
        string? credential,
        CancellationToken cancellationToken) =>
        await ValidateAsync(credential, cancellationToken)
            ? new(ProviderCredentialValidationKind.Valid)
            : new(ProviderCredentialValidationKind.InvalidCredential);
    Task<AiExecutionResult> ExecuteAsync(AiExecutionRequest request, string? credential, CancellationToken cancellationToken);
    IAsyncEnumerable<AiExecutionChunk> ExecuteStreamingAsync(
        AiExecutionRequest request,
        string? credential,
        CancellationToken cancellationToken) =>
        AiProviderStreaming.BufferAsync(this, request, credential, cancellationToken);
    Task<string?> RefreshCredentialAsync(string? credential, CancellationToken cancellationToken) =>
        Task.FromResult(credential);
}
public static class AiProviderStreaming
{
    public static async IAsyncEnumerable<AiExecutionChunk> BufferAsync(
        IAiProvider provider,
        AiExecutionRequest request,
        string? credential,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var result = await provider.ExecuteAsync(request, credential, cancellationToken);
        yield return new(result.Output, true, result);
    }
}
public interface IToolExecutor
{
    Task<ToolInvocationResult> ExecuteAsync(
        ToolCallRequest request,
        string idempotencyKey,
        CancellationToken cancellationToken);
}
public interface IProviderMetadataCanonicalizer
{
    int MaximumCheckpointUtf8Bytes { get; }
    AiExecutionResult Canonicalize(AiExecutionResult result, string fallbackModel);
}
public sealed class ProviderMetadataCanonicalizer(
    Func<string?, string?> canonicalizeRequestId,
    Func<string?, string?> canonicalizeActualModel,
    int maximumCheckpointUtf8Bytes) : IProviderMetadataCanonicalizer
{
    public int MaximumCheckpointUtf8Bytes { get; } = maximumCheckpointUtf8Bytes > 0
        ? maximumCheckpointUtf8Bytes
        : throw new ArgumentOutOfRangeException(nameof(maximumCheckpointUtf8Bytes));

    public AiExecutionResult Canonicalize(AiExecutionResult result, string fallbackModel) => result with
    {
        ProviderRequestId = canonicalizeRequestId(result.ProviderRequestId),
        ActualModel = canonicalizeActualModel(result.ActualModel),
    };
}
