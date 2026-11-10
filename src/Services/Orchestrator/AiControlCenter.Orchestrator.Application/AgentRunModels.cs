using AiControlCenter.Orchestrator.Domain;

namespace AiControlCenter.Orchestrator.Application;

public sealed record CreateAgentRunCommand(
    Guid AgentId,
    string Input,
    TestRunOutcome? ExpectedOutcome,
    Guid OwnerUserId,
    string CorrelationId,
    string DelegatedAuthorization);

public sealed record ListAgentRunsQuery(
    Guid RequestUserId,
    bool CanViewAll,
    AgentRunStatus? Status = null,
    Guid? AgentId = null,
    int Page = 1,
    int PageSize = 20)
{
    public const int MaximumPageSize = 100;
}

public sealed record GetAgentRunQuery(Guid Id, Guid RequestUserId, bool CanViewAll);
public sealed record GetAgentRunOperationsQuery(Guid RequestUserId, bool CanViewAll, int RecentLimit = 8)
{
    public const int MaximumRecentLimit = 20;
}
public sealed record BeginAgentRunCommand(Guid RunId, Guid MessageId);
public sealed record ReportRunStepCommand(
    Guid RunId,
    Guid MessageId,
    int Sequence,
    string Name,
    RunStepStatus Status,
    string? Log);
public sealed record CompleteAgentRunCommand(
    Guid RunId,
    Guid MessageId,
    AgentRunStatus Status,
    string? Result,
    string? Error);
public sealed record FailAgentRunCommand(Guid RunId, Guid MessageId, string Error);
public sealed record ReportAiAttemptCommand(Guid RunId, Guid MessageId, int Sequence, string Provider,
    string Model, string Status, string? FailureKind, string? ProviderRequestId, int InputTokens,
    int OutputTokens, decimal? EstimatedCostUsd, string? ActualModel, string IdempotencyKey,
    int RoutePriority, long LatencyMilliseconds, int? CachedInputTokens, string UsageSource,
    string CostSource, string FinishStatus, string? FallbackReason, string? PartialOutput,
    string? CheckpointJson, string? ErrorCode = null, string? Operation = null,
    string? GenerationDispatched = null, string? SafeExceptionType = null);
public sealed record PauseAgentRunCommand(Guid RunId, Guid MessageId, AgentRunStatus Status, string Reason);
public sealed record ResumeAgentRunCommand(
    Guid RunId,
    Guid RequestUserId,
    bool CanManageAll,
    Guid OperationId);
public sealed record PreflightAgentRunQuery(Guid RunId, Guid RequestUserId, bool CanManageAll);
public sealed record AgentRunPreflightDto(bool Available, string Code);
public sealed record AgentRunResumeSafetyDto(bool Allowed, string Code);

public sealed record RunStepDto(
    Guid Id,
    int Sequence,
    string Name,
    RunStepStatus Status,
    string? Log,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);
public sealed record AiProviderAttemptDto(Guid Id, int Sequence, string Provider, string Model, string Status,
    string? FailureKind, string? ProviderRequestId, int InputTokens, int OutputTokens,
    decimal? EstimatedCostUsd, string? ActualModel, string IdempotencyKey, int RoutePriority,
    long LatencyMilliseconds, int? CachedInputTokens, int TotalTokens, string UsageSource,
    string CostSource, string FinishStatus, string? FallbackReason, string? PartialOutput,
    DateTimeOffset StartedAt, DateTimeOffset CompletedAt, string? ErrorCode,
    string? Operation, string? GenerationDispatched, string? SafeExceptionType);

public sealed record AgentRunDto(
    Guid Id,
    Guid AgentId,
    Guid OwnerUserId,
    string CorrelationId,
    string AgentName,
    string AgentCode,
    long AgentVersion,
    Guid DirectionId,
    string DirectionName,
    string DirectionCode,
    long DirectionVersion,
    string ExecutionType,
    string Input,
    TestRunOutcome ExpectedOutcome,
    AgentRunStatus Status,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string? Result,
    string? Error,
    IReadOnlyCollection<RunStepDto> Steps,
    IReadOnlyCollection<AiProviderAttemptDto> Attempts,
    string? ModelRoutesJson,
    int MaxAttempts,
    decimal? MaxEstimatedCostUsd,
    string? CheckpointJson,
    int CheckpointVersion,
    string? CheckpointHash,
    DateTimeOffset? CheckpointUpdatedAt);

public sealed record AgentRunListDto(
    IReadOnlyCollection<AgentRunDto> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling((double)TotalCount / PageSize);
}

public sealed record AgentRunOperationsItemDto(
    Guid Id,
    string AgentName,
    string DirectionName,
    AgentRunStatus Status,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? ActiveStepSequence,
    string? ActiveStepName,
    RunStepStatus? ActiveStepStatus);

public sealed record AgentRunOperationsDto(
    int Queued,
    int Running,
    int Succeeded,
    int Failed,
    IReadOnlyCollection<AgentRunOperationsItemDto> RecentRuns);

public sealed record RunProgressResult(bool Accepted, bool AlreadyApplied, long Revision, AgentRunStatus Status);
