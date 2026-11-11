using AiControlCenter.Integrations.Domain;

namespace AiControlCenter.Integrations.Application;

public sealed record ProviderConnectionDto(Guid Id, AiProviderKind Provider, string DisplayName,
    ProviderConnectionStatus Status, string? CredentialFingerprint, DateTimeOffset? LastValidatedAt,
    DateTimeOffset? LastSyncedAt, DateTimeOffset? RevokedAt, int CredentialVersion,
    ProviderMetricSource QuotaSource, string QuotaState, string? LastErrorCategory,
    string? LastSafeError, string? ExternalAccountId, string? ExternalAccountLogin, uint Version,
    DateTimeOffset? CredentialExpiresAt, string ExpirationSource,
    DateTimeOffset? LastSuccessfulValidationAt, DateTimeOffset? LastModelSyncAt,
    DateTimeOffset? NextValidationAt, string AuthenticationStatus,
    string ModelCatalogStatus, string CredentialStatusSource);
public sealed record ProviderModelDto(string Id, string DisplayName, int? ContextWindow, bool SupportsTools,
    bool SupportsStreaming, decimal? InputUsdPerMillionTokens, decimal? OutputUsdPerMillionTokens);
public enum ProviderCredentialValidationKind
{
    Valid,
    InvalidCredential,
    RequiresReconnect,
    NotEntitled,
    PolicyBlocked,
    OAuthAppUnsupported,
    InsufficientPermissions,
    QuotaExhausted,
    RateLimited,
    TemporarilyUnavailable,
    ModelCatalogUnavailable,
    UnknownFailure,
}
public sealed record ProviderCredentialValidationResult(
    ProviderCredentialValidationKind Kind,
    IReadOnlyCollection<ProviderModelDto>? Models = null,
    string? ExternalAccountId = null,
    string? ExternalAccountLogin = null,
    ProviderMetricSource QuotaSource = ProviderMetricSource.Unavailable,
    string QuotaState = "Unavailable");
public sealed class ProviderCredentialRejectedException(string code, string safeMessage)
    : InvalidOperationException(safeMessage)
{
    public string Code { get; } = string.IsNullOrWhiteSpace(code)
        ? throw new ArgumentException("A safe error code is required.", nameof(code))
        : code;
}
public enum ProviderFailureKind
{
    None, Validation, Authentication, Safety, Cancelled, Tool, Configuration, LocalBudgetExceeded, AmbiguousTimeout,
    QuotaExceeded, RateLimited, TemporarilyUnavailable, ModelUnavailable, InsufficientFunds,
}
public sealed record AiExecutionRequest(Guid OwnerUserId, Guid ConnectionId, string ModelId, string SystemPrompt,
    string Input, IReadOnlyCollection<string> AllowedTools, string? Scenario, string IdempotencyKey,
    decimal? DailyCostCapUsd = null, decimal? MonthlyCostCapUsd = null,
    int? MaxRequestsPerDay = null, int? MaxRequestsPerMonth = null,
    int? MaxInputTokens = null, int? MaxOutputTokens = null,
    decimal? RunCostCapUsd = null, decimal? RouteCostCapUsd = null,
    AiProviderKind? ExpectedProvider = null,
    IReadOnlyCollection<ToolDefinition>? ToolDefinitions = null,
    IReadOnlyCollection<ToolInvocationResult>? PriorToolResults = null,
    int MaxToolCycles = 4,
    int CheckpointToolAuditBudgetBytes = 0);
public sealed record AiExecutionResult(bool Succeeded, string? Output, ProviderFailureKind FailureKind,
    string? ErrorCode, string? ProviderRequestId, int InputTokens, int OutputTokens,
    decimal? EstimatedCostUsd, IReadOnlyCollection<ToolInvocationResult> ToolInvocations,
    string? ActualModel = null, long LatencyMilliseconds = 0, int? CachedInputTokens = null,
    ProviderMetricSource UsageSource = ProviderMetricSource.Unavailable,
    ProviderMetricSource CostSource = ProviderMetricSource.Unavailable,
    string FinishStatus = "Unknown", string? PartialOutput = null,
    IReadOnlyCollection<ToolCallRequest>? RequestedToolCalls = null,
    int OutboundRequestCount = 0,
    string? Operation = null,
    string GenerationDispatched = "false",
    string? SafeExceptionType = null,
    IReadOnlyCollection<ProviderOperationDiagnostic>? OperationDiagnostics = null);
public sealed record ProviderOperationDiagnostic(
    string Operation,
    string Marker,
    long DurationMilliseconds,
    string ResultCode,
    string? SafeExceptionType,
    string? CorrelationId,
    string? ProviderRequestId,
    string GenerationDispatched,
    string? SafeMetadata = null);
public sealed record AiExecutionChunk(string? Delta, bool IsFinal, AiExecutionResult? Result);
public sealed record ResumeSafetyInspectionResult(bool Allowed, string Code);
public sealed record ToolDefinition(string Name, string Description, string InputJsonSchema);
public sealed record ToolCallRequest(string CallId, string ToolName, string ArgumentsJson);
public sealed record ToolInvocationResult(
    string ToolName,
    bool Succeeded,
    string? Output,
    string? Error,
    string CallId = "",
    string ArgumentsJson = "{}",
    string Status = "Completed",
    DateTimeOffset? StartedAt = null,
    DateTimeOffset? CompletedAt = null,
    string IdempotencyKey = "");

public static class SafeToolCatalog
{
    private static readonly Dictionary<string, ToolDefinition> Definitions =
        new Dictionary<string, ToolDefinition>(StringComparer.Ordinal)
        {
            ["system.utc-time"] = new("system.utc-time", "Return the current server UTC time.",
                "{\"type\":\"object\",\"properties\":{},\"additionalProperties\":false}"),
            ["text.length"] = new("text.length", "Return the character length of a bounded text value.",
                "{\"type\":\"object\",\"properties\":{\"text\":{\"type\":\"string\",\"maxLength\":4096}},\"required\":[\"text\"],\"additionalProperties\":false}"),
        };
    public static IReadOnlyCollection<ToolDefinition> Resolve(IReadOnlyCollection<string> allowedTools) =>
        allowedTools.Distinct(StringComparer.Ordinal)
            .Where(Definitions.ContainsKey).Select(name => Definitions[name]).ToArray();
}
public sealed record ProviderUsageSummaryDto(int RequestCount, int InputTokens, int? CachedInputTokens,
    int OutputTokens, decimal EstimatedCostUsd, DateTimeOffset Since,
    ProviderMetricSource UsageSource, ProviderMetricSource CostSource);
public static class ProviderFallbackPolicy
{
    public static bool CanFallback(ProviderFailureKind failure) => failure is ProviderFailureKind.QuotaExceeded
        or ProviderFailureKind.RateLimited or ProviderFailureKind.TemporarilyUnavailable
        or ProviderFailureKind.ModelUnavailable or ProviderFailureKind.InsufficientFunds;
}

public static class ProviderPricingCatalog
{
    public static (decimal Input, decimal Output)? Rates(AiProviderKind provider, string model)
    {
        model = model.ToLowerInvariant();
        if (provider == AiProviderKind.OpenAi)
        {
            if (model.StartsWith("gpt-5-nano", StringComparison.Ordinal)) return (0.05m, 0.40m);
            if (model.StartsWith("gpt-5-mini", StringComparison.Ordinal)) return (0.25m, 2m);
            if (model.StartsWith("gpt-5", StringComparison.Ordinal)) return (1.25m, 10m);
        }
        if (provider == AiProviderKind.XAi)
        {
            if (model.StartsWith("grok-build", StringComparison.Ordinal) || model.StartsWith("grok-code-fast", StringComparison.Ordinal)) return (1m, 2m);
            if (model.StartsWith("grok-4.3", StringComparison.Ordinal) || model.StartsWith("grok-4.20", StringComparison.Ordinal)) return (1.25m, 2.50m);
            if (model.StartsWith("grok-4.5", StringComparison.Ordinal) || model.StartsWith("grok-4.6", StringComparison.Ordinal) || model.StartsWith("grok-4.7", StringComparison.Ordinal)) return (2m, 6m);
        }
        return null;
    }
    public static decimal? Estimate(AiProviderKind provider, string model, int inputTokens, int outputTokens)
    {
        var rates = Rates(provider, model);
        return rates is null ? null : decimal.Round((inputTokens * rates.Value.Input + outputTokens * rates.Value.Output) / 1_000_000m, 8);
    }
}
