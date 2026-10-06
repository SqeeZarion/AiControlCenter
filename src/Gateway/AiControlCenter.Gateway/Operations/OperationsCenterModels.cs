using System.Text.Json.Serialization;

namespace AiControlCenter.Gateway.Operations;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperationsConnectionState
{
    Connected,
    Degraded,
    Disconnected,
    Unknown,
}

public sealed record OperationsHealthCheckDto(string Name, string Status, double DurationMilliseconds);

public sealed record OperationsNodeDto(
    string Id,
    string Label,
    string Kind,
    OperationsConnectionState State,
    double? LatencyMilliseconds,
    DateTimeOffset LastCheckedAt,
    DateTimeOffset? LastSuccessfulAt,
    IReadOnlyCollection<OperationsHealthCheckDto> Checks);

public sealed record OperationsQueueDto(
    string Name,
    OperationsConnectionState State,
    int? MessagesReady,
    int? MessagesUnacknowledged,
    int? Consumers,
    DateTimeOffset LastCheckedAt,
    DateTimeOffset? LastSuccessfulAt);

public sealed record OperationsRunDto(
    Guid Id,
    string AgentName,
    string DirectionName,
    string Status,
    long Revision,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    int? ActiveStepSequence,
    string? ActiveStepName,
    string? ActiveStepStatus);

public sealed record OperationsWorkloadDto(
    int? TotalAgents,
    int? ActiveAgents,
    int? QueuedRuns,
    int? RunningRuns,
    int? SucceededRuns,
    int? FailedRuns,
    IReadOnlyCollection<OperationsRunDto> RecentRuns);

public sealed record OperationsCenterSnapshotDto(
    DateTimeOffset GeneratedAt,
    int FreshnessSeconds,
    IReadOnlyCollection<OperationsNodeDto> Nodes,
    IReadOnlyCollection<OperationsQueueDto> Queues,
    OperationsWorkloadDto Workload);
