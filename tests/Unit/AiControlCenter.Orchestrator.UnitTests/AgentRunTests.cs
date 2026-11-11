using AiControlCenter.Orchestrator.Domain;

namespace AiControlCenter.Orchestrator.UnitTests;

public sealed class AgentRunTests
{

    [Fact]
    public void PausingRunPausesItsActiveStepAndResumeCanRestartIt()
    {
        var run = Create();
        run.Begin(MessageId, Now.AddSeconds(1));
        run.ReportStep(MessageId, 1, "AI execution", RunStepStatus.Running,
            "AI provider execution started.", Now.AddSeconds(2));

        run.Pause(MessageId, AgentRunStatus.PausedProviderUnavailable,
            "Provider unavailable.", Now.AddSeconds(3));

        var step = Assert.Single(run.Steps);
        Assert.Equal(RunStepStatus.Paused, step.Status);
        Assert.Null(step.CompletedAt);
        Assert.Equal(RunTransitionResult.AlreadyApplied,
            run.Begin(MessageId, Now.AddSeconds(4)));
        Assert.Equal(AgentRunStatus.PausedProviderUnavailable, run.Status);

        var resumedMessageId = Guid.NewGuid();
        run.Resume(resumedMessageId);
        run.Begin(resumedMessageId, Now.AddSeconds(4));
        run.ReportStep(resumedMessageId, 1, "AI execution", RunStepStatus.Running,
            "AI provider execution resumed.", Now.AddSeconds(5));

        Assert.Equal(RunStepStatus.Running, step.Status);
        Assert.Null(step.CompletedAt);
    }

    [Fact]
    public void PreGenerationUnavailableAttemptIsRetriedInPlaceAfterResume()
    {
        var run = Create();
        run.Begin(MessageId, Now.AddSeconds(1));
        run.ReportStep(MessageId, 1, "AI execution", RunStepStatus.Running, null, Now.AddSeconds(2));
        run.RecordAttempt(MessageId, 1, "GitHubCopilotLocalCli", "Auto", "Failed",
            "TemporarilyUnavailable", null, 0, 0, null, "auto", "first-message", 1, 0, null,
            "Unavailable", "Unavailable", "Unknown", "TemporarilyUnavailable", null, null,
            Now.AddSeconds(3));
        run.Pause(MessageId, AgentRunStatus.PausedProviderUnavailable,
            "Provider unavailable.", Now.AddSeconds(4));

        Assert.Equal(0, run.AttemptOffset);
        var resumedMessage = Guid.NewGuid();
        run.Resume(resumedMessage);
        run.Begin(resumedMessage, Now.AddSeconds(5));
        run.RecordAttempt(resumedMessage, 1, "GitHubCopilotLocalCli", "Auto", "Succeeded",
            null, "provider-request", 1, 1, null, "auto", "second-message", 1, 25, null,
            "ProviderReported", "Unavailable", "Completed", null, null, null,
            Now.AddSeconds(6));

        var attempt = Assert.Single(run.Attempts);
        Assert.Equal("Succeeded", attempt.Status);
        Assert.Equal("second-message", attempt.IdempotencyKey);
        Assert.Equal(1, run.AttemptOffset);
    }
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid MessageId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Fact]
    public void SuccessfulLifecycleKeepsImmutableSnapshotAndOrderedSteps()
    {
        var run = Create();

        Assert.Equal(RunTransitionResult.Applied, run.Begin(MessageId, Now.AddSeconds(1)));
        Assert.Equal(RunTransitionResult.Applied, run.ReportStep(MessageId, 1, "Validate", RunStepStatus.Running, "start", Now.AddSeconds(2)));
        Assert.Equal(RunTransitionResult.Applied, run.ReportStep(MessageId, 1, "Validate", RunStepStatus.Succeeded, "done", Now.AddSeconds(3)));
        Assert.Equal(RunTransitionResult.Applied, run.Complete(MessageId, AgentRunStatus.Succeeded, "ok", null, Now.AddSeconds(4)));

        Assert.Equal(AgentRunStatus.Succeeded, run.Status);
        Assert.Equal("Snapshot agent", run.AgentName);
        Assert.Single(run.Steps);
        Assert.Equal(5, run.Revision);
    }

    [Fact]
    public void RedeliveryWithSameMessageAndPayloadIsIdempotent()
    {
        var run = Create();
        run.Begin(MessageId, Now.AddSeconds(1));
        run.ReportStep(MessageId, 1, "Validate", RunStepStatus.Running, "start", Now.AddSeconds(2));
        run.ReportStep(MessageId, 1, "Validate", RunStepStatus.Succeeded, "done", Now.AddSeconds(3));
        run.Complete(MessageId, AgentRunStatus.Succeeded, "ok", null, Now.AddSeconds(4));
        var revision = run.Revision;

        Assert.Equal(RunTransitionResult.AlreadyApplied, run.Begin(MessageId, Now.AddSeconds(5)));
        Assert.Equal(RunTransitionResult.AlreadyApplied, run.Complete(MessageId, AgentRunStatus.Succeeded, "ignored", null, Now.AddSeconds(5)));
        Assert.Equal(revision, run.Revision);
    }

    [Fact]
    public void DifferentMessageCannotOwnRunAndStepsMustBeSequential()
    {
        var run = Create();
        run.Begin(MessageId, Now.AddSeconds(1));

        Assert.Throws<AgentRunRuleViolationException>(() => run.Begin(Guid.NewGuid(), Now.AddSeconds(2)));
        Assert.Throws<AgentRunRuleViolationException>(() =>
            run.ReportStep(MessageId, 2, "Skipped", RunStepStatus.Running, null, Now.AddSeconds(2)));
    }

    [Fact]
    public void FailureBeforeStartIsAllowedAndTerminalCannotReverse()
    {
        var run = Create();

        run.FailBeforeOrDuringStart(MessageId, "unavailable", Now.AddSeconds(1));

        Assert.Equal(AgentRunStatus.Failed, run.Status);
        Assert.Null(run.StartedAt);
        Assert.Throws<AgentRunRuleViolationException>(() =>
            run.Complete(MessageId, AgentRunStatus.Succeeded, "no", null, Now.AddSeconds(2)));
    }

    [Fact]
    public void InvalidCompletionDoesNotPartiallyMutateRun()
    {
        var run = Create();
        run.Begin(MessageId, Now.AddSeconds(1));

        Assert.Throws<AgentRunRuleViolationException>(() =>
            run.Complete(MessageId, AgentRunStatus.Failed, null, null, Now.AddSeconds(2)));

        Assert.Equal(AgentRunStatus.Running, run.Status);
        Assert.Null(run.CompletedAt);
        Assert.Null(run.Error);
    }

    [Fact]
    public void FailedCompletionAtomicallyTerminatesTheActiveStep()
    {
        var run = Create();
        run.Begin(MessageId, Now.AddSeconds(1));
        run.ReportStep(MessageId, 1, "AI execution", RunStepStatus.Running,
            "Provider execution started.", Now.AddSeconds(2));

        var result = run.Complete(MessageId, AgentRunStatus.Failed, null,
            "SessionCreate failed before generation dispatch.", Now.AddSeconds(3));

        Assert.Equal(RunTransitionResult.Applied, result);
        Assert.Equal(AgentRunStatus.Failed, run.Status);
        var step = Assert.Single(run.Steps);
        Assert.Equal(RunStepStatus.Failed, step.Status);
        Assert.Equal("SessionCreate failed before generation dispatch.", step.Log);
        Assert.Equal(Now.AddSeconds(3), step.CompletedAt);
    }

    [Fact]
    public void PredispatchAttemptPersistsSafeDiagnosticsWithoutClaimingAnActualModel()
    {
        var run = Create();
        run.Begin(MessageId, Now.AddSeconds(1));

        run.RecordAttempt(MessageId, 1, "GitHubCopilotLocalCli", "Auto", "Failed",
            "Configuration", null, 0, 0, null, null, "idempotency", 1, 170, null,
            "Unavailable", "Unavailable", "Rejected", "Configuration", null, null,
            Now.AddSeconds(2), "session_create_failed", "SessionCreate", "false",
            "ProviderRuntimeException");

        var attempt = Assert.Single(run.Attempts);
        Assert.Equal("session_create_failed", attempt.ErrorCode);
        Assert.Equal("SessionCreate", attempt.Operation);
        Assert.Equal("false", attempt.GenerationDispatched);
        Assert.Equal("ProviderRuntimeException", attempt.SafeExceptionType);
        Assert.Null(attempt.ActualModel);
    }

    [Fact]
    public void TimestampsCannotMoveBackwards()
    {
        var run = Create();
        Assert.Throws<ArgumentOutOfRangeException>(() => run.Begin(MessageId, Now.AddTicks(-1)));
        run.Begin(MessageId, Now.AddSeconds(1));
        run.ReportStep(MessageId, 1, "Validate", RunStepStatus.Running, null, Now.AddSeconds(2));
        Assert.Throws<AgentRunRuleViolationException>(() =>
            run.ReportStep(MessageId, 1, "Validate", RunStepStatus.Succeeded, null, Now.AddSeconds(1)));
    }

    [Fact]
    public void AggregateTimelineRejectsBackwardTimestampsAcrossStepsAndCompletion()
    {
        var run = Create();
        run.Begin(MessageId, Now.AddSeconds(1));
        run.ReportStep(MessageId, 1, "First", RunStepStatus.Running, null, Now.AddSeconds(2));
        run.ReportStep(MessageId, 1, "First", RunStepStatus.Succeeded, null, Now.AddSeconds(4));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            run.ReportStep(MessageId, 2, "Second", RunStepStatus.Running, null, Now.AddSeconds(3)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            run.Complete(MessageId, AgentRunStatus.Succeeded, "ok", null, Now.AddSeconds(3)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            run.FailBeforeOrDuringStart(MessageId, "failed", Now.AddSeconds(3)));
    }

    [Fact]
    public void AggregateTimelineAllowsEqualAndIncreasingTimestampsWithoutBreakingIdempotency()
    {
        var run = Create();
        run.Begin(MessageId, Now.AddSeconds(1));
        run.ReportStep(MessageId, 1, "First", RunStepStatus.Running, null, Now.AddSeconds(2));
        run.ReportStep(MessageId, 1, "First", RunStepStatus.Succeeded, null, Now.AddSeconds(2));
        run.ReportStep(MessageId, 2, "Second", RunStepStatus.Running, null, Now.AddSeconds(2));
        run.ReportStep(MessageId, 2, "Second", RunStepStatus.Succeeded, null, Now.AddSeconds(3));
        var revision = run.Revision;

        Assert.Equal(
            RunTransitionResult.AlreadyApplied,
            run.ReportStep(MessageId, 2, "Second", RunStepStatus.Succeeded, null, Now.AddSeconds(3)));
        Assert.Equal(revision, run.Revision);
        Assert.Equal(
            RunTransitionResult.Applied,
            run.Complete(MessageId, AgentRunStatus.Succeeded, "ok", null, Now.AddSeconds(4)));
    }

    [Fact]
    public void BoundedFieldsRejectOversizedData()
    {
        Assert.Throws<ArgumentException>(() => AgentRun.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
            Snapshot(), new string('x', AgentRun.InputMaxLength + 1), TestRunOutcome.Succeed, Now));
        var run = Create();
        run.Begin(MessageId, Now.AddSeconds(1));
        Assert.Throws<ArgumentException>(() => run.ReportStep(
            MessageId, 1, "Validate", RunStepStatus.Running,
            new string('x', RunStep.LogMaxLength + 1), Now.AddSeconds(2)));
    }

    private static AgentRun Create() => AgentRun.Create(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation",
        Snapshot(), "input", TestRunOutcome.Succeed, Now);

    private static AgentSnapshot Snapshot() => new(
        "Snapshot agent", "snapshot-agent", "description", 3, "Test",
        Guid.NewGuid(), "Direction", "direction", 7);
}
