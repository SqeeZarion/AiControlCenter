using AiControlCenter.Orchestrator.Domain;

namespace AiControlCenter.Orchestrator.UnitTests;

public sealed class AgentRunTests
{
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
