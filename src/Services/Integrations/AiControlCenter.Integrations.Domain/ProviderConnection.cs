namespace AiControlCenter.Integrations.Domain;

public enum AiProviderKind { GitHubCopilot, GitHubCopilotLocalCli, OpenAi, XAi, DeterministicTest }
public enum ProviderConnectionStatus
{
    Active,
    Degraded,
    RequiresReconnect,
    NotEntitled,
    PolicyBlocked,
    OAuthAppUnsupported,
    InsufficientPermissions,
    QuotaExhausted,
    Invalid,
    Revoked,
}
public enum ProviderMetricSource { ProviderReported, Estimated, Unavailable }

public sealed class ProviderConnection
{
    public const int DisplayNameMaxLength = 100;
    public const int SecretMaxLength = 16_384;
    public const int ExternalAccountIdMaxLength = 64;
    public const int ExternalAccountLoginMaxLength = 100;
    private ProviderConnection() { }
    private ProviderConnection(Guid id, Guid ownerUserId, AiProviderKind provider, string displayName,
        string? protectedCredential, string? credentialFingerprint, DateTimeOffset now)
    {
        Id = id; OwnerUserId = ownerUserId; Provider = provider;
        DisplayName = NormalizeDisplayName(displayName); ProtectedCredential = protectedCredential;
        CredentialFingerprint = credentialFingerprint; Status = ProviderConnectionStatus.Active;
        CredentialVersion = 1; QuotaSource = ProviderMetricSource.Unavailable; QuotaState = "Unavailable";
        CreatedAt = now; UpdatedAt = now;
    }
    public Guid Id { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public AiProviderKind Provider { get; private set; }
    public string DisplayName { get; private set; } = string.Empty;
    public string? ProtectedCredential { get; private set; }
    public string? CredentialFingerprint { get; private set; }
    public ProviderConnectionStatus Status { get; private set; }
    public int CredentialVersion { get; private set; }
    public string? AvailableModelsJson { get; private set; }
    public DateTimeOffset? LastSyncedAt { get; private set; }
    public ProviderMetricSource QuotaSource { get; private set; }
    public string QuotaState { get; private set; } = "Unavailable";
    public string? LastErrorCategory { get; private set; }
    public string? LastSafeError { get; private set; }
    public string? ExternalAccountId { get; private set; }
    public string? ExternalAccountLogin { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? LastValidatedAt { get; private set; }
    public DateTimeOffset? LastSuccessfulValidationAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public uint Version { get; private set; }
    public static ProviderConnection Create(Guid ownerUserId, AiProviderKind provider, string displayName,
        string? protectedCredential, string? credentialFingerprint, DateTimeOffset now, Guid? connectionId = null)
    {
        if (ownerUserId == Guid.Empty) throw new ArgumentException("Owner is required.", nameof(ownerUserId));
        if (!Enum.IsDefined(provider))
            throw new ArgumentOutOfRangeException(nameof(provider), "Unknown provider type.");
        if (provider is not (AiProviderKind.DeterministicTest or AiProviderKind.GitHubCopilotLocalCli)
            && string.IsNullOrWhiteSpace(protectedCredential))
            throw new ArgumentException("A provider credential is required.", nameof(protectedCredential));
        if (protectedCredential?.Length > SecretMaxLength) throw new ArgumentException("Protected credential is too large.", nameof(protectedCredential));
        if (connectionId == Guid.Empty) throw new ArgumentException("Connection id is required.", nameof(connectionId));
        return new(connectionId ?? Guid.NewGuid(), ownerUserId, provider, displayName,
            protectedCredential, credentialFingerprint, now);
    }
    public void ReplaceCredential(string protectedCredential, string fingerprint, DateTimeOffset now)
    {
        if (Provider == AiProviderKind.DeterministicTest) throw new InvalidOperationException("No credential is used.");
        if (string.IsNullOrWhiteSpace(protectedCredential) || protectedCredential.Length > SecretMaxLength) throw new ArgumentException("Invalid credential.");
        ProtectedCredential = protectedCredential; CredentialFingerprint = fingerprint; CredentialVersion++;
        Status = ProviderConnectionStatus.Active; RevokedAt = null; AvailableModelsJson = null; LastSyncedAt = null;
        LastErrorCategory = LastSafeError = ExternalAccountId = ExternalAccountLogin = null; UpdatedAt = now;
    }
    public void ReactivateWithoutCredential(DateTimeOffset now)
    {
        if (Provider is not (AiProviderKind.DeterministicTest or AiProviderKind.GitHubCopilotLocalCli))
            throw new InvalidOperationException("This provider requires a credential.");
        ProtectedCredential = null;
        CredentialFingerprint = null;
        Status = ProviderConnectionStatus.Active;
        RevokedAt = null;
        AvailableModelsJson = null;
        LastSyncedAt = null;
        LastErrorCategory = LastSafeError = ExternalAccountId = ExternalAccountLogin = null;
        UpdatedAt = now;
    }
    public void MarkValidated(bool valid, DateTimeOffset now)
    {
        Status = valid ? ProviderConnectionStatus.Active : ProviderConnectionStatus.Invalid;
        LastValidatedAt = now; UpdatedAt = now;
        if (valid) LastSuccessfulValidationAt = now.ToUniversalTime();
        LastErrorCategory = valid ? null : "Authentication";
        LastSafeError = valid ? null : "Credential validation failed.";
    }
    public void MarkValidationFailed(
        ProviderConnectionStatus status,
        string category,
        string safeMessage,
        DateTimeOffset now)
    {
        if (status is ProviderConnectionStatus.Active or ProviderConnectionStatus.Revoked)
            throw new ArgumentOutOfRangeException(nameof(status));
        if (string.IsNullOrWhiteSpace(category)) throw new ArgumentException("Category is required.", nameof(category));
        if (string.IsNullOrWhiteSpace(safeMessage)) throw new ArgumentException("Safe message is required.", nameof(safeMessage));
        Status = status;
        LastValidatedAt = now;
        UpdatedAt = now;
        LastErrorCategory = category;
        LastSafeError = safeMessage;
    }
    public void BindExternalAccount(string externalAccountId, string externalAccountLogin, DateTimeOffset now)
    {
        var id = externalAccountId?.Trim() ?? string.Empty;
        var login = externalAccountLogin?.Trim() ?? string.Empty;
        if (id.Length is < 1 or > ExternalAccountIdMaxLength)
            throw new ArgumentException("External account id is invalid.", nameof(externalAccountId));
        if (login.Length is < 1 or > ExternalAccountLoginMaxLength)
            throw new ArgumentException("External account login is invalid.", nameof(externalAccountLogin));
        ExternalAccountId = id;
        ExternalAccountLogin = login;
        UpdatedAt = now;
    }
    public void UpdateQuota(ProviderMetricSource source, string state, DateTimeOffset now)
    {
        if (!Enum.IsDefined(source)) throw new ArgumentOutOfRangeException(nameof(source));
        var normalized = state?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 64) throw new ArgumentException("Quota state is invalid.", nameof(state));
        QuotaSource = source;
        QuotaState = normalized;
        UpdatedAt = now;
    }
    public void SynchronizeModels(string modelsJson, DateTimeOffset now)
    {
        if (Status == ProviderConnectionStatus.Revoked) throw new InvalidOperationException("Connection is revoked.");
        if (string.IsNullOrWhiteSpace(modelsJson) || modelsJson.Length > 65_536)
            throw new ArgumentException("Model catalog is invalid.", nameof(modelsJson));
        AvailableModelsJson = modelsJson; LastSyncedAt = now.ToUniversalTime(); UpdatedAt = LastSyncedAt.Value;
        LastErrorCategory = LastSafeError = null;
    }
    public void Revoke(DateTimeOffset now)
    {
        ProtectedCredential = null; AvailableModelsJson = null; Status = ProviderConnectionStatus.Revoked;
        ExternalAccountId = ExternalAccountLogin = null;
        RevokedAt = now; LastSyncedAt = null; LastErrorCategory = LastSafeError = null; UpdatedAt = now;
    }
    private static string NormalizeDisplayName(string value)
    {
        var result = value?.Trim() ?? string.Empty;
        if (result.Length is < 2 or > DisplayNameMaxLength) throw new ArgumentException("Invalid display name.", nameof(value));
        return result;
    }
}

public sealed class ProviderOAuthState
{
    private ProviderOAuthState() { }
    public ProviderOAuthState(Guid id, Guid ownerUserId, string stateHash, string codeVerifierProtected, string redirectUri, DateTimeOffset expiresAt)
    { Id = id; OwnerUserId = ownerUserId; StateHash = stateHash; CodeVerifierProtected = codeVerifierProtected; RedirectUri = redirectUri; ExpiresAt = expiresAt; }
    public Guid Id { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public string StateHash { get; private set; } = string.Empty;
    public string CodeVerifierProtected { get; private set; } = string.Empty;
    public string RedirectUri { get; private set; } = string.Empty;
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public long Version { get; private set; }
    public bool TryConsume(DateTimeOffset now)
    { if (ConsumedAt is not null || now >= ExpiresAt) return false; ConsumedAt = now; Version++; return true; }
}

public enum ProviderExecutionReceiptStatus { Pending, Completed, Released, Ambiguous }

public sealed class ProviderExecutionReceipt
{
    public const int IdempotencyKeyMaxLength = 160;
    public const int ResultJsonMaxLength = 65_536;
    private ProviderExecutionReceipt() { }
    private ProviderExecutionReceipt(Guid id, Guid ownerUserId, Guid connectionId, AiProviderKind provider,
        string idempotencyKey, decimal reservedCostUsd, int reservedRequestCount, DateTimeOffset now)
    {
        Id = id;
        OwnerUserId = ownerUserId;
        ConnectionId = connectionId;
        Provider = provider;
        IdempotencyKey = Require(idempotencyKey, IdempotencyKeyMaxLength, nameof(idempotencyKey));
        Status = ProviderExecutionReceiptStatus.Pending;
        ReservedCostUsd = reservedCostUsd;
        ReservedRequestCount = reservedRequestCount;
        CreatedAt = now.ToUniversalTime();
    }
    public Guid Id { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public Guid ConnectionId { get; private set; }
    public AiProviderKind Provider { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public ProviderExecutionReceiptStatus Status { get; private set; }
    public string? ResultJson { get; private set; }
    public decimal ReservedCostUsd { get; private set; }
    public decimal? ActualCostUsd { get; private set; }
    public int ReservedRequestCount { get; private set; }
    public int ActualRequestCount { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public uint Version { get; private set; }
    public static ProviderExecutionReceipt Start(
        Guid ownerUserId,
        Guid connectionId,
        string idempotencyKey,
        DateTimeOffset now,
        AiProviderKind provider = AiProviderKind.DeterministicTest,
        decimal reservedCostUsd = 0,
        int reservedRequestCount = 1)
    {
        if (ownerUserId == Guid.Empty || connectionId == Guid.Empty)
            throw new ArgumentException("Owner and connection are required.");
        if (!Enum.IsDefined(provider)) throw new ArgumentOutOfRangeException(nameof(provider));
        ArgumentOutOfRangeException.ThrowIfNegative(reservedCostUsd);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(reservedRequestCount);
        return new(Guid.NewGuid(), ownerUserId, connectionId, provider, idempotencyKey,
            reservedCostUsd, reservedRequestCount, now);
    }
    public void ReserveAdditionalRequests(int count)
    {
        if (Status != ProviderExecutionReceiptStatus.Pending)
            throw new InvalidOperationException("Only pending executions can reserve requests.");
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        ReservedRequestCount = checked(ReservedRequestCount + count);
    }
    public void Complete(string resultJson, DateTimeOffset now, decimal? actualCostUsd = null,
        int actualRequestCount = 0)
    {
        if (Status == ProviderExecutionReceiptStatus.Completed) return;
        ResultJson = Require(resultJson, ResultJsonMaxLength, nameof(resultJson));
        SetActualRequestCount(actualRequestCount);
        Status = ProviderExecutionReceiptStatus.Completed;
        ActualCostUsd = actualCostUsd;
        CompletedAt = now.ToUniversalTime();
    }
    public void Release(string resultJson, DateTimeOffset now, int actualRequestCount = 0)
    {
        if (Status != ProviderExecutionReceiptStatus.Pending) return;
        ResultJson = Require(resultJson, ResultJsonMaxLength, nameof(resultJson));
        SetActualRequestCount(actualRequestCount);
        ActualCostUsd = 0;
        Status = ProviderExecutionReceiptStatus.Released;
        CompletedAt = now.ToUniversalTime();
    }
    public void MarkAmbiguous(string resultJson, DateTimeOffset now, int actualRequestCount = 0)
    {
        if (Status != ProviderExecutionReceiptStatus.Pending) return;
        ResultJson = Require(resultJson, ResultJsonMaxLength, nameof(resultJson));
        SetActualRequestCount(actualRequestCount);
        Status = ProviderExecutionReceiptStatus.Ambiguous;
        CompletedAt = now.ToUniversalTime();
    }
    private void SetActualRequestCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > ReservedRequestCount)
            throw new ArgumentOutOfRangeException(nameof(count), "Actual requests cannot exceed reserved requests.");
        ActualRequestCount = count;
    }
    private static string Require(string? value, int maxLength, string name)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > maxLength
            ? throw new ArgumentException($"Value must contain 1-{maxLength} characters.", name)
            : normalized;
    }
}
