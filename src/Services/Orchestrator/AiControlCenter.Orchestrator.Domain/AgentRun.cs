namespace AiControlCenter.Orchestrator.Domain;

public enum AgentRunStatus
{
    Queued = 0,
    Running = 1,
    Succeeded = 2,
    Failed = 3,
}

public enum RunStepStatus
{
    Running = 0,
    Succeeded = 1,
    Failed = 2,
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

    private readonly List<RunStep> _steps = [];

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
        ExecutionType = snapshot.ExecutionType == "Test"
            ? snapshot.ExecutionType
            : throw new ArgumentException("Only Test execution is supported.", nameof(snapshot));
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
    public uint Version { get; private set; }
    public IReadOnlyCollection<RunStep> Steps => _steps.AsReadOnly();

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
        if (Status is AgentRunStatus.Running or AgentRunStatus.Succeeded or AgentRunStatus.Failed)
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
    long DirectionVersion);

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
            if (string.Equals(Log, log, StringComparison.Ordinal)) return false;
            Log = log;
            UpdatedAt = timestamp;
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
