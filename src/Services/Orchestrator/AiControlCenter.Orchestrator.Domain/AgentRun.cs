using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace AiControlCenter.Orchestrator.Domain;

public enum AgentRunStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
    PausedProviderUnavailable = 4,
    PausedInsufficientFunds = 5,
    Cancelled = 6,
}

public enum RunStepStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
    Paused = 3,
}

public enum TestRunOutcome
{
    Succeed = 0,
    Fail = 1,
}

public enum RunTransitionResult
{
    Applied = 0,
    AlreadyApplied = 1,
}

public sealed class AgentRun
{
    public const int NameMaxLength = 100;
    public const int CodeMaxLength = 64;
    public const int DescriptionMaxLength = 1000;
    public const int InputMaxLength = 500;
    public const int CorrelationIdMaxLength = 128;
    public const int ResultMaxLength = 4000;
    public const int ErrorMaxLength = 1000;
    public const int MaximumSteps = 16;
    public const int CheckpointJsonMaxLength = 65_536;

    private readonly List<RunStep> _steps = [];
    private readonly List<AiProviderAttempt> _attempts = [];

    private AgentRun()
    {
    }

    private AgentRun(
        Guid id,
        Guid agentId,
        Guid ownerUserId,
        string correlationId,
        AgentSnapshot snapshot,
        string input,
        TestRunOutcome expectedOutcome,
        DateTimeOffset now)
    {
        Id = RequireId(id, nameof(id));
        AgentId = RequireId(agentId, nameof(agentId));
        OwnerUserId = RequireId(ownerUserId, nameof(ownerUserId));
        CorrelationId = RequireBounded(correlationId, CorrelationIdMaxLength, nameof(correlationId));
        AgentName = RequireBounded(snapshot.AgentName, NameMaxLength, nameof(snapshot.AgentName));
        AgentCode = RequireBounded(snapshot.AgentCode, CodeMaxLength, nameof(snapshot.AgentCode));
        AgentDescription = OptionalBounded(snapshot.AgentDescription, DescriptionMaxLength, nameof(snapshot.AgentDescription));
        AgentVersion = RequireVersion(snapshot.AgentVersion, nameof(snapshot.AgentVersion));
        DirectionId = RequireId(snapshot.DirectionId, nameof(snapshot.DirectionId));
        DirectionName = RequireBounded(snapshot.DirectionName, NameMaxLength, nameof(snapshot.DirectionName));
        DirectionCode = RequireBounded(snapshot.DirectionCode, CodeMaxLength, nameof(snapshot.DirectionCode));
        DirectionVersion = RequireVersion(snapshot.DirectionVersion, nameof(snapshot.DirectionVersion));
        ExecutionType = snapshot.ExecutionType is "Test" or "Ai"
            ? snapshot.ExecutionType
            : throw new ArgumentException("Unknown execution type.", nameof(snapshot));
        SystemPrompt = snapshot.SystemPrompt;
        ModelRoutesJson = snapshot.ModelRoutesJson;
        ToolPolicyJson = snapshot.ToolPolicyJson;
        MaxInputTokens = snapshot.MaxInputTokens;
        MaxOutputTokens = snapshot.MaxOutputTokens;
        MaxEstimatedCostUsd = snapshot.MaxEstimatedCostUsd;
        MaxAttempts = snapshot.MaxAttempts;
        Input = OptionalBounded(input, InputMaxLength, nameof(input)) ?? string.Empty;
        ExpectedOutcome = Enum.IsDefined(expectedOutcome)
            ? expectedOutcome
            : throw new ArgumentOutOfRangeException(nameof(expectedOutcome));
        Status = AgentRunStatus.Queued;
        Revision = 1;
        CreatedAt = now.ToUniversalTime();
    }

    public Guid Id { get; private set; }
    public Guid AgentId { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string AgentName { get; private set; } = string.Empty;
    public string AgentCode { get; private set; } = string.Empty;
    public string? AgentDescription { get; private set; }
    public long AgentVersion { get; private set; }
    public Guid DirectionId { get; private set; }
    public string DirectionName { get; private set; } = string.Empty;
    public string DirectionCode { get; private set; } = string.Empty;
    public long DirectionVersion { get; private set; }
    public string ExecutionType { get; private set; } = string.Empty;
    public string? SystemPrompt { get; private set; }
    public string? ModelRoutesJson { get; private set; }
    public string? ToolPolicyJson { get; private set; }
    public int? MaxInputTokens { get; private set; }
    public int? MaxOutputTokens { get; private set; }
    public decimal? MaxEstimatedCostUsd { get; private set; }
    public int MaxAttempts { get; private set; } = 1;
    public string Input { get; private set; } = string.Empty;
    public TestRunOutcome ExpectedOutcome { get; private set; }
    public AgentRunStatus Status { get; private set; }
    public Guid? ExecutionMessageId { get; private set; }
    public long Revision { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? Result { get; private set; }
    public string? Error { get; private set; }
    public string? CheckpointJson { get; private set; }
    public int CheckpointVersion { get; private set; }
    public string? CheckpointHash { get; private set; }
    public DateTimeOffset? CheckpointUpdatedAt { get; private set; }
    public uint Version { get; private set; }
    public IReadOnlyCollection<RunStep> Steps => _steps.AsReadOnly();
    public IReadOnlyCollection<AiProviderAttempt> Attempts => _attempts.AsReadOnly();
    public int AttemptOffset => _attempts.LastOrDefault()?.CanRetryWithoutNewSequence == true
        ? _attempts.Count - 1 : _attempts.Count;

    public RunTransitionResult RecordAttempt(Guid messageId, int sequence, string provider, string model,
        string status, string? failureKind, string? providerRequestId, int inputTokens, int outputTokens,
        decimal? estimatedCostUsd, string? actualModel, string idempotencyKey, int routePriority,
        long latencyMilliseconds, int? cachedInputTokens, string usageSource, string costSource,
        string finishStatus, string? fallbackReason, string? partialOutput, string? checkpointJson,
        DateTimeOffset now, string? errorCode = null, string? operation = null,
        string? generationDispatched = null, string? safeExceptionType = null)
    {
        EnsureExecution(messageId);
        if (routePriority <= 0 || latencyMilliseconds < 0 || inputTokens < 0 || outputTokens < 0
            || cachedInputTokens < 0 || cachedInputTokens > inputTokens || estimatedCostUsd < 0)
            throw new AgentRunRuleViolationException("Attempt metrics are invalid.");
        if (usageSource is not ("ProviderReported" or "Estimated" or "Unavailable")
            || costSource is not ("ProviderReported" or "Estimated" or "Unavailable"))
            throw new AgentRunRuleViolationException("Attempt metric source is invalid.");
        var existing = _attempts.SingleOrDefault(x => x.Sequence == sequence);
        if (existing is not null)
        {
            if (!existing.CanRetryWithoutNewSequence) return RunTransitionResult.AlreadyApplied;
            existing.ReplaceRetry(provider, model, actualModel, status, failureKind,
                providerRequestId, inputTokens, cachedInputTokens, outputTokens,
                estimatedCostUsd, idempotencyKey, routePriority, latencyMilliseconds,
                usageSource, costSource, finishStatus, fallbackReason, partialOutput,
                errorCode, operation, generationDispatched, safeExceptionType, now);
            if (!string.IsNullOrWhiteSpace(checkpointJson)) ApplyCheckpoint(checkpointJson, sequence, now);
            Revision++;
            return RunTransitionResult.Applied;
        }
        if (sequence != _attempts.Count + 1 || sequence > MaxAttempts)
            throw new AgentRunRuleViolationException("Invalid attempt sequence.");
        _attempts.Add(AiProviderAttempt.Create(Guid.NewGuid(), Id, OwnerUserId, sequence, provider, model,
            actualModel, status, failureKind, providerRequestId, inputTokens, cachedInputTokens,
            outputTokens, estimatedCostUsd, idempotencyKey, routePriority, CorrelationId,
            latencyMilliseconds, usageSource, costSource, finishStatus, fallbackReason, partialOutput,
            errorCode, operation, generationDispatched, safeExceptionType, now));
        if (!string.IsNullOrWhiteSpace(checkpointJson)) ApplyCheckpoint(checkpointJson, sequence, now);
        Revision++; return RunTransitionResult.Applied;
    }

    private void ApplyCheckpoint(string checkpointJson, int sequence, DateTimeOffset now)
    {
        if (Encoding.UTF8.GetByteCount(checkpointJson) > CheckpointJsonMaxLength)
            throw new AgentRunRuleViolationException("Checkpoint exceeds the allowed size.");
        JsonNode root;
        try { root = JsonNode.Parse(checkpointJson) ?? throw new JsonException(); }
        catch (JsonException) { throw new AgentRunRuleViolationException("Checkpoint JSON is malformed."); }
        if (root["schemaVersion"]?.GetValue<int>() != 1
            || root["checkpointVersion"]?.GetValue<int>() is not int version
            || version <= CheckpointVersion
            || root["resumeCursor"]?.GetValue<int>() != sequence
            || root["hash"]?.GetValue<string>() is not string hash
            || hash.Length != 64 || hash.Any(character => !Uri.IsHexDigit(character)))
            throw new AgentRunRuleViolationException("Checkpoint metadata is invalid.");
        var unsigned = root.DeepClone();
        unsigned.AsObject().Remove("hash");
        var expectedHash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(unsigned.ToJsonString()))).ToLowerInvariant();
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(hash), Encoding.ASCII.GetBytes(expectedHash)))
            throw new AgentRunRuleViolationException("Checkpoint integrity check failed.");
        var routeSnapshot = root["routeSnapshot"];
        if (routeSnapshot is null || ModelRoutesJson is null
            || !JsonNode.DeepEquals(routeSnapshot, JsonNode.Parse(ModelRoutesJson)))
            throw new AgentRunRuleViolationException("Checkpoint route snapshot does not match the immutable run snapshot.");
        CheckpointJson = checkpointJson;
        CheckpointVersion = version;
        CheckpointHash = hash;
        CheckpointUpdatedAt = now.ToUniversalTime();
    }

    public RunTransitionResult Pause(Guid messageId, AgentRunStatus pausedStatus, string reason, DateTimeOffset now)
    {
        EnsureExecution(messageId);
        if (pausedStatus is not (AgentRunStatus.PausedProviderUnavailable or AgentRunStatus.PausedInsufficientFunds))
            throw new AgentRunRuleViolationException("Invalid paused status.");
        if (Status == pausedStatus) return RunTransitionResult.AlreadyApplied;
        if (Status != AgentRunStatus.Running) throw new AgentRunRuleViolationException("Only a running run can pause.");
        var activeStep = _steps.SingleOrDefault(step => step.Status == RunStepStatus.Running);
        activeStep?.Apply(activeStep.Name, RunStepStatus.Paused, reason, now);
        Status = pausedStatus; Error = RequireBounded(reason, ErrorMaxLength, nameof(reason)); Revision++; return RunTransitionResult.Applied;
    }

    public RunTransitionResult Resume(Guid newMessageId)
    {
        RequireId(newMessageId, nameof(newMessageId));
        if (Status is not (AgentRunStatus.PausedProviderUnavailable or AgentRunStatus.PausedInsufficientFunds))
            throw new AgentRunRuleViolationException("Only a paused run can resume.");
        Status = AgentRunStatus.Queued; ExecutionMessageId = newMessageId; StartedAt = null; Error = null; Revision++; return RunTransitionResult.Applied;
    }

    public static AgentRun Create(
        Guid id,
        Guid agentId,
        Guid ownerUserId,
        string correlationId,
        AgentSnapshot snapshot,
        string input,
        TestRunOutcome expectedOutcome,
        DateTimeOffset now) =>
        new(id, agentId, ownerUserId, correlationId, snapshot, input, expectedOutcome, now);

    public RunTransitionResult Begin(Guid messageId, DateTimeOffset now)
    {
        RequireId(messageId, nameof(messageId));
        if (ExecutionMessageId is not null && ExecutionMessageId != messageId)
            throw new AgentRunRuleViolationException("Run is already owned by another execution message.");
        if (Status is AgentRunStatus.Running or AgentRunStatus.Succeeded or AgentRunStatus.Failed
            or AgentRunStatus.PausedProviderUnavailable or AgentRunStatus.PausedInsufficientFunds)
            return RunTransitionResult.AlreadyApplied;

        var timestamp = RequireAtOrAfter(now, CreatedAt, nameof(now));
        Status = AgentRunStatus.Running;
        ExecutionMessageId = messageId;
        StartedAt = timestamp;
        Revision++;
        return RunTransitionResult.Applied;
    }

    public RunTransitionResult ReportStep(
        Guid messageId,
        int sequence,
        string name,
        RunStepStatus status,
        string? log,
        DateTimeOffset now)
    {
        EnsureExecution(messageId);
        if (Status != AgentRunStatus.Running)
            throw new AgentRunRuleViolationException("Steps can be reported only while the run is Running.");
        if (sequence is < 1 or > MaximumSteps)
            throw new AgentRunRuleViolationException($"Step sequence must be between 1 and {MaximumSteps}.");
        if (!Enum.IsDefined(status)) throw new AgentRunRuleViolationException("Unknown step status.");

        var existing = _steps.SingleOrDefault(step => step.Sequence == sequence);
        var normalizedName = RequireBounded(name, RunStep.NameMaxLength, nameof(name));
        var normalizedLog = OptionalBounded(log, RunStep.LogMaxLength, nameof(log));
        var timestamp = existing is null
            ? RequireAtOrAfter(now, LatestAppliedAt(), nameof(now))
            : now.ToUniversalTime();
        if (existing is not null)
        {
            var changed = existing.Apply(normalizedName, status, normalizedLog, timestamp);
            if (!changed) return RunTransitionResult.AlreadyApplied;
            Revision++;
            return RunTransitionResult.Applied;
        }

        if (sequence != _steps.Count + 1 || status != RunStepStatus.Running)
            throw new AgentRunRuleViolationException("A new step must start next in sequence with Running status.");
        if (_steps.LastOrDefault()?.Status is RunStepStatus.Running or RunStepStatus.Failed)
            throw new AgentRunRuleViolationException("The previous step must succeed before another step starts.");

        _steps.Add(RunStep.Start(Guid.NewGuid(), Id, sequence, normalizedName, normalizedLog, timestamp));
        Revision++;
        return RunTransitionResult.Applied;
    }

    public RunTransitionResult Complete(
        Guid messageId,
        AgentRunStatus terminalStatus,
        string? result,
        string? error,
        DateTimeOffset now)
    {
        EnsureExecution(messageId);
        if (Status is AgentRunStatus.Succeeded or AgentRunStatus.Failed)
        {
            return Status == terminalStatus
                ? RunTransitionResult.AlreadyApplied
                : throw new AgentRunRuleViolationException("A terminal run cannot change its outcome.");
        }
        if (Status != AgentRunStatus.Running
            || terminalStatus is not (AgentRunStatus.Succeeded or AgentRunStatus.Failed))
            throw new AgentRunRuleViolationException("Only a Running run can complete.");
        if (terminalStatus == AgentRunStatus.Succeeded
            && (_steps.Count == 0 || _steps.Any(step => step.Status != RunStepStatus.Succeeded)))
            throw new AgentRunRuleViolationException("A run can succeed only after all steps succeed.");

        var normalizedResult = OptionalBounded(result, ResultMaxLength, nameof(result));
        var normalizedError = OptionalBounded(error, ErrorMaxLength, nameof(error));
        if (terminalStatus == AgentRunStatus.Succeeded && normalizedError is not null)
            throw new AgentRunRuleViolationException("A successful run cannot contain an error.");
        if (terminalStatus == AgentRunStatus.Failed && normalizedError is null)
            throw new AgentRunRuleViolationException("A failed run requires an error reason.");
        var timestamp = RequireAtOrAfter(now, LatestAppliedAt(), nameof(now));
        if (terminalStatus == AgentRunStatus.Failed)
        {
            var activeStep = _steps.SingleOrDefault(step => step.Status == RunStepStatus.Running);
            activeStep?.Apply(activeStep.Name, RunStepStatus.Failed, normalizedError, timestamp);
        }
        Status = terminalStatus;
        Result = normalizedResult;
        Error = normalizedError;
        CompletedAt = timestamp;
        Revision++;
        return RunTransitionResult.Applied;
    }

    public RunTransitionResult FailBeforeOrDuringStart(Guid messageId, string error, DateTimeOffset now)
    {
        RequireId(messageId, nameof(messageId));
        if (ExecutionMessageId is not null && ExecutionMessageId != messageId)
            throw new AgentRunRuleViolationException("Run is already owned by another execution message.");
        if (Status == AgentRunStatus.Failed) return RunTransitionResult.AlreadyApplied;
        if (Status == AgentRunStatus.Succeeded)
            throw new AgentRunRuleViolationException("A successful run cannot be failed.");

        var timestamp = RequireAtOrAfter(now, LatestAppliedAt(), nameof(now));
        var normalizedError = RequireBounded(error, ErrorMaxLength, nameof(error));
        ExecutionMessageId ??= messageId;
        Status = AgentRunStatus.Failed;
        Error = normalizedError;
        CompletedAt = timestamp;
        Revision++;
        return RunTransitionResult.Applied;
    }

    private void EnsureExecution(Guid messageId)
    {
        RequireId(messageId, nameof(messageId));
        if (ExecutionMessageId != messageId)
            throw new AgentRunRuleViolationException("Execution message does not own this run.");
    }

    private DateTimeOffset LatestAppliedAt()
    {
        var latest = StartedAt ?? CreatedAt;
        foreach (var step in _steps)
        {
            if (step.UpdatedAt > latest) latest = step.UpdatedAt;
        }

        return latest;
    }

    private static Guid RequireId(Guid value, string name) =>
        value == Guid.Empty ? throw new ArgumentException("A non-empty id is required.", name) : value;

    private static long RequireVersion(long value, string name) =>
        value <= 0 ? throw new ArgumentOutOfRangeException(name, "Version must be positive.") : value;

    private static DateTimeOffset RequireAtOrAfter(
        DateTimeOffset value,
        DateTimeOffset minimum,
        string name)
    {
        var utc = value.ToUniversalTime();
        return utc < minimum
            ? throw new ArgumentOutOfRangeException(name, "Timestamp cannot move backwards.")
            : utc;
    }

    internal static string RequireBounded(string? value, int maxLength, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized) || normalized.Length > maxLength)
            throw new ArgumentException($"Value must contain 1-{maxLength} characters.", name);
        return normalized;
    }

    internal static string? OptionalBounded(string? value, int maxLength, string name)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrEmpty(normalized)) return null;
        if (normalized.Length > maxLength)
            throw new ArgumentException($"Value cannot exceed {maxLength} characters.", name);
        return normalized;
    }
}

public sealed record AgentSnapshot(
    string AgentName,
    string AgentCode,
    string? AgentDescription,
    long AgentVersion,
    string ExecutionType,
    Guid DirectionId,
    string DirectionName,
    string DirectionCode,
    long DirectionVersion,
    string? SystemPrompt = null,
    string? ModelRoutesJson = null,
    string? ToolPolicyJson = null,
    int? MaxInputTokens = null,
    int? MaxOutputTokens = null,
    decimal? MaxEstimatedCostUsd = null,
    int MaxAttempts = 1);

public sealed class AiProviderAttempt
{
    private AiProviderAttempt() { }
    private AiProviderAttempt(Guid id, Guid runId, Guid ownerUserId, int sequence, string provider,
        string model, string? actualModel, string status, string? failureKind, string? requestId,
        int inputTokens, int? cachedInputTokens, int outputTokens, decimal? cost, string idempotencyKey,
        int routePriority, string correlationId, long latencyMilliseconds, string usageSource,
        string costSource, string finishStatus, string? fallbackReason, string? partialOutput,
        string? errorCode, string? operation, string? generationDispatched,
        string? safeExceptionType, DateTimeOffset now)
    {
        Id = id; AgentRunId = runId; OwnerUserId = ownerUserId; Sequence = sequence;
        Provider = AgentRun.RequireBounded(provider, 32, nameof(provider));
        Model = AgentRun.RequireBounded(model, 128, nameof(model));
        ActualModel = AgentRun.OptionalBounded(actualModel, 128, nameof(actualModel));
        Status = AgentRun.RequireBounded(status, 32, nameof(status));
        FailureKind = AgentRun.OptionalBounded(failureKind, 64, nameof(failureKind));
        ProviderRequestId = AgentRun.OptionalBounded(requestId, 256, nameof(requestId));
        InputTokens = inputTokens; CachedInputTokens = cachedInputTokens;
        OutputTokens = outputTokens; TotalTokens = inputTokens + outputTokens; EstimatedCostUsd = cost;
        IdempotencyKey = AgentRun.RequireBounded(idempotencyKey, 160, nameof(idempotencyKey));
        RoutePriority = routePriority;
        CorrelationId = AgentRun.RequireBounded(correlationId, AgentRun.CorrelationIdMaxLength, nameof(correlationId));
        LatencyMilliseconds = latencyMilliseconds; UsageSource = usageSource; CostSource = costSource;
        FinishStatus = AgentRun.RequireBounded(finishStatus, 32, nameof(finishStatus));
        FallbackReason = AgentRun.OptionalBounded(fallbackReason, 64, nameof(fallbackReason));
        PartialOutput = AgentRun.OptionalBounded(partialOutput, AgentRun.ResultMaxLength, nameof(partialOutput));
        ErrorCode = AgentRun.OptionalBounded(errorCode, 128, nameof(errorCode));
        Operation = AgentRun.OptionalBounded(operation, 64, nameof(operation));
        GenerationDispatched = NormalizeGenerationDispatched(generationDispatched);
        SafeExceptionType = AgentRun.OptionalBounded(safeExceptionType, 128, nameof(safeExceptionType));
        CompletedAt = now.ToUniversalTime(); StartedAt = CompletedAt.AddMilliseconds(-latencyMilliseconds);
    }
    public Guid Id { get; private set; }
    public Guid AgentRunId { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public int Sequence { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string Model { get; private set; } = string.Empty;
    public string? ActualModel { get; private set; }
    public string Status { get; private set; } = string.Empty;
    public string? FailureKind { get; private set; }
    public string? ProviderRequestId { get; private set; }
    public int InputTokens { get; private set; }
    public int? CachedInputTokens { get; private set; }
    public int OutputTokens { get; private set; }
    public int TotalTokens { get; private set; }
    public decimal? EstimatedCostUsd { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public int RoutePriority { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public long LatencyMilliseconds { get; private set; }
    public int RequestCount { get; private set; } = 1;
    public string UsageSource { get; private set; } = string.Empty;
    public string CostSource { get; private set; } = string.Empty;
    public string FinishStatus { get; private set; } = string.Empty;
    public string? FallbackReason { get; private set; }
    public string? PartialOutput { get; private set; }
    public string? ErrorCode { get; private set; }
    public string? Operation { get; private set; }
    public string? GenerationDispatched { get; private set; }
    public string? SafeExceptionType { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset CompletedAt { get; private set; }
    public bool CanRetryWithoutNewSequence => Status == "Failed"
        && FailureKind == "TemporarilyUnavailable"
        && ProviderRequestId is null
        && InputTokens == 0 && OutputTokens == 0
        && EstimatedCostUsd is null
        && LatencyMilliseconds == 0;
    public static AiProviderAttempt Create(Guid id, Guid runId, Guid ownerUserId, int sequence,
        string provider, string model, string? actualModel, string status, string? failureKind,
        string? requestId, int inputTokens, int? cachedInputTokens, int outputTokens, decimal? cost,
        string idempotencyKey, int routePriority, string correlationId, long latencyMilliseconds,
        string usageSource, string costSource, string finishStatus, string? fallbackReason,
        string? partialOutput, string? errorCode, string? operation, string? generationDispatched,
        string? safeExceptionType, DateTimeOffset now) => new(id, runId, ownerUserId, sequence, provider,
            model, actualModel, status, failureKind, requestId, inputTokens, cachedInputTokens,
            outputTokens, cost, idempotencyKey, routePriority, correlationId, latencyMilliseconds,
            usageSource, costSource, finishStatus, fallbackReason, partialOutput,
            errorCode, operation, generationDispatched, safeExceptionType, now);

    internal void ReplaceRetry(string provider, string model, string? actualModel,
        string status, string? failureKind, string? requestId, int inputTokens,
        int? cachedInputTokens, int outputTokens, decimal? cost, string idempotencyKey,
        int routePriority, long latencyMilliseconds, string usageSource, string costSource,
        string finishStatus, string? fallbackReason, string? partialOutput,
        string? errorCode, string? operation, string? generationDispatched,
        string? safeExceptionType, DateTimeOffset now)
    {
        if (!CanRetryWithoutNewSequence)
            throw new AgentRunRuleViolationException("Only a pre-generation unavailable attempt can be retried in place.");
        Provider = AgentRun.RequireBounded(provider, 32, nameof(provider));
        Model = AgentRun.RequireBounded(model, 128, nameof(model));
        ActualModel = AgentRun.OptionalBounded(actualModel, 128, nameof(actualModel));
        Status = AgentRun.RequireBounded(status, 32, nameof(status));
        FailureKind = AgentRun.OptionalBounded(failureKind, 64, nameof(failureKind));
        ProviderRequestId = AgentRun.OptionalBounded(requestId, 256, nameof(requestId));
        InputTokens = inputTokens;
        CachedInputTokens = cachedInputTokens;
        OutputTokens = outputTokens;
        TotalTokens = inputTokens + outputTokens;
        EstimatedCostUsd = cost;
        IdempotencyKey = AgentRun.RequireBounded(idempotencyKey, 160, nameof(idempotencyKey));
        RoutePriority = routePriority;
        LatencyMilliseconds = latencyMilliseconds;
        UsageSource = AgentRun.RequireBounded(usageSource, 24, nameof(usageSource));
        CostSource = AgentRun.RequireBounded(costSource, 24, nameof(costSource));
        FinishStatus = AgentRun.RequireBounded(finishStatus, 32, nameof(finishStatus));
        FallbackReason = AgentRun.OptionalBounded(fallbackReason, 64, nameof(fallbackReason));
        PartialOutput = AgentRun.OptionalBounded(partialOutput, AgentRun.ResultMaxLength, nameof(partialOutput));
        ErrorCode = AgentRun.OptionalBounded(errorCode, 128, nameof(errorCode));
        Operation = AgentRun.OptionalBounded(operation, 64, nameof(operation));
        GenerationDispatched = NormalizeGenerationDispatched(generationDispatched);
        SafeExceptionType = AgentRun.OptionalBounded(safeExceptionType, 128, nameof(safeExceptionType));
        CompletedAt = now.ToUniversalTime();
        StartedAt = CompletedAt.AddMilliseconds(-latencyMilliseconds);
    }

    private static string? NormalizeGenerationDispatched(string? value) => value switch
    {
        null => null,
        "true" or "false" or "unknown" => value,
        _ => throw new AgentRunRuleViolationException("Generation dispatch state is invalid."),
    };
}

public sealed class RunStep
{
    public const int NameMaxLength = 100;
    public const int LogMaxLength = 2000;

    private RunStep()
    {
    }

    private RunStep(Guid id, Guid runId, int sequence, string name, string? log, DateTimeOffset now)
    {
        Id = id;
        AgentRunId = runId;
        Sequence = sequence;
        Name = name;
        Status = RunStepStatus.Running;
        Log = log;
        StartedAt = now.ToUniversalTime();
        UpdatedAt = StartedAt;
    }

    public Guid Id { get; private set; }
    public Guid AgentRunId { get; private set; }
    public int Sequence { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public RunStepStatus Status { get; private set; }
    public string? Log { get; private set; }
    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }

    internal static RunStep Start(
        Guid id, Guid runId, int sequence, string name, string? log, DateTimeOffset now) =>
        new(id, runId, sequence, name, log, now);

    internal bool Apply(string name, RunStepStatus status, string? log, DateTimeOffset now)
    {
        if (!string.Equals(Name, name, StringComparison.Ordinal))
            throw new AgentRunRuleViolationException("A step name cannot change.");
        var timestamp = now.ToUniversalTime();
        if (timestamp < UpdatedAt)
            throw new AgentRunRuleViolationException("A step timestamp cannot move backwards.");
        if (Status is RunStepStatus.Succeeded or RunStepStatus.Failed)
        {
            if (status == RunStepStatus.Running && string.Equals(Name, name, StringComparison.Ordinal)) return false;
            if (Status == status && string.Equals(Log, log, StringComparison.Ordinal)) return false;
            throw new AgentRunRuleViolationException("A terminal step cannot change.");
        }
        if (status == RunStepStatus.Running)
        {
            if (Status == RunStepStatus.Running && string.Equals(Log, log, StringComparison.Ordinal)) return false;
            Status = RunStepStatus.Running;
            Log = log;
            UpdatedAt = timestamp;
            return true;
        }

        if (status == RunStepStatus.Paused)
        {
            Status = status;
            Log = log;
            UpdatedAt = timestamp;
            CompletedAt = null;
            return true;
        }

        Status = status;
        Log = log;
        UpdatedAt = timestamp;
        CompletedAt = UpdatedAt;
        return true;
    }
}

public sealed class AgentRunRuleViolationException(string message) : InvalidOperationException(message);
