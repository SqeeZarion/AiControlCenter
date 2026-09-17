using AiControlCenter.Orchestrator.Domain;

namespace AiControlCenter.Orchestrator.Application;

public sealed record CreateAgentRunCommand(
    Guid AgentId,
    string Input,
    TestRunOutcome ExpectedOutcome,
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

public sealed record RunStepDto(
    Guid Id,
    int Sequence,
    string Name,
    RunStepStatus Status,
    string? Log,
    DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt);

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
    IReadOnlyCollection<RunStepDto> Steps);

public sealed record AgentRunListDto(
    IReadOnlyCollection<AgentRunDto> Items,
    int Page,
    int PageSize,
    int TotalCount)
{
    public int TotalPages => TotalCount == 0 ? 0 : (int)Math.Ceiling((double)TotalCount / PageSize);
}

public sealed record RunProgressResult(bool Accepted, bool AlreadyApplied, long Revision, AgentRunStatus Status);
