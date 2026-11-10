using AiControlCenter.Orchestrator.Domain;
using FluentValidation;

namespace AiControlCenter.Orchestrator.Application;

public sealed class AgentRunApplicationService(
    IAgentRunRepository runs,
    IOrchestratorUnitOfWork unitOfWork,
    IAgentCatalogClient agentCatalog,
    IAiProviderRouteResolver routeResolver,
    IRunTransport transport,
    TimeProvider timeProvider,
    IValidator<CreateAgentRunCommand> createValidator,
    IValidator<ListAgentRunsQuery> listValidator)
{
    public async Task<AgentRunDto> CreateAsync(
        CreateAgentRunCommand command,
        CancellationToken cancellationToken)
    {
        await createValidator.ValidateAndThrowAsync(command, cancellationToken);
        var snapshot = await agentCatalog.GetRunnableAgentAsync(
            command.AgentId,
            command.DelegatedAuthorization,
            cancellationToken);
        var expectedOutcome = snapshot.ExecutionType switch
        {
            "Ai" when string.IsNullOrWhiteSpace(command.Input) => throw new ValidationException(
                "An AI agent requires a prompt."),
            "Ai" => TestRunOutcome.Succeed,
            "Test" when command.ExpectedOutcome is not null => command.ExpectedOutcome.Value,
            "Test" => throw new ValidationException(
                "A deterministic Test agent requires an expected outcome."),
            _ => throw new AgentRunConflictException(
                "The persisted agent execution type is not supported."),
        };
        if (string.Equals(snapshot.ExecutionType, "Ai", StringComparison.Ordinal))
        {
            var logicalRoutes = snapshot.ModelRoutesJson
                ?? throw new AgentRunConflictException("AI agent routes are missing.");
            snapshot = snapshot with
            {
                ModelRoutesJson = await routeResolver.ResolveAsync(
                    command.OwnerUserId, logicalRoutes, cancellationToken),
            };
        }
        var run = AgentRun.Create(
            Guid.NewGuid(), command.AgentId, command.OwnerUserId, command.CorrelationId,
            snapshot, command.Input, expectedOutcome, timeProvider.GetUtcNow());
        var messageId = Guid.NewGuid();
        runs.Add(run);
        await transport.QueueAsync(run, messageId, cancellationToken);
        await transport.PublishStatusAsync(run, null, run.CreatedAt, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);
        return ToDto(run);
    }

    public async Task<AgentRunListDto> ListAsync(
        ListAgentRunsQuery query,
        CancellationToken cancellationToken)
    {
        await listValidator.ValidateAndThrowAsync(query, cancellationToken);
        var page = await runs.ListAsync(query, cancellationToken);
        return new AgentRunListDto(
            page.Items.Select(ToDto).ToArray(),
            query.Page,
            query.PageSize,
            page.TotalCount);
    }

    public async Task<AgentRunDto> GetAsync(GetAgentRunQuery query, CancellationToken cancellationToken)
    {
        if (query.Id == Guid.Empty || query.RequestUserId == Guid.Empty)
            throw new ValidationException("A valid run and user id are required.");
        var run = await runs.GetAsync(query.Id, false, cancellationToken)
            ?? throw new AgentRunNotFoundException(query.Id);
        if (!query.CanViewAll && run.OwnerUserId != query.RequestUserId)
            throw new AgentRunAccessDeniedException();
        return ToDto(run);
    }

    public async Task<AgentRunOperationsDto> GetOperationsAsync(
        GetAgentRunOperationsQuery query,
        CancellationToken cancellationToken)
    {
        if (query.RequestUserId == Guid.Empty)
            throw new ValidationException("A valid user id is required.");
        if (query.RecentLimit is < 1 or > GetAgentRunOperationsQuery.MaximumRecentLimit)
            throw new ValidationException(
                $"Recent limit must be between 1 and {GetAgentRunOperationsQuery.MaximumRecentLimit}.");

        var data = await runs.GetOperationsAsync(
            query.RequestUserId,
            query.CanViewAll,
            query.RecentLimit,
            cancellationToken);
        return new AgentRunOperationsDto(
            Count(data, AgentRunStatus.Queued),
            Count(data, AgentRunStatus.Running),
            Count(data, AgentRunStatus.Succeeded),
            Count(data, AgentRunStatus.Failed),
            data.RecentRuns.Select(ToOperationsDto).ToArray());
    }

    public Task<RunProgressResult> BeginAsync(
        BeginAgentRunCommand command,
        CancellationToken cancellationToken) =>
        MutateAsync(command.RunId, async (run, now, token) =>
        {
            var result = run.Begin(command.MessageId, now);
            if (result == RunTransitionResult.Applied)
                await transport.PublishStatusAsync(run, null, now, token);
            return result;
        }, cancellationToken);

    public Task<RunProgressResult> ReportStepAsync(
        ReportRunStepCommand command,
        CancellationToken cancellationToken) =>
        MutateAsync(command.RunId, async (run, now, token) =>
        {
            var result = run.ReportStep(
                command.MessageId, command.Sequence, command.Name, command.Status, command.Log, now);
            var step = run.Steps.Single(item => item.Sequence == command.Sequence);
            if (result == RunTransitionResult.Applied)
                await transport.PublishStatusAsync(run, step, now, token);
            return result;
        }, cancellationToken);

    public Task<RunProgressResult> ReportAttemptAsync(ReportAiAttemptCommand command, CancellationToken cancellationToken) =>
        MutateAsync(command.RunId, async (run, now, token) =>
        {
            var result = run.RecordAttempt(command.MessageId, command.Sequence, command.Provider, command.Model,
                command.Status, command.FailureKind, command.ProviderRequestId, command.InputTokens,
                command.OutputTokens, command.EstimatedCostUsd, command.ActualModel,
                command.IdempotencyKey, command.RoutePriority, command.LatencyMilliseconds,
                command.CachedInputTokens, command.UsageSource, command.CostSource,
                command.FinishStatus, command.FallbackReason, command.PartialOutput,
                command.CheckpointJson, now, command.ErrorCode, command.Operation,
                command.GenerationDispatched, command.SafeExceptionType);
            if (result == RunTransitionResult.Applied) await transport.PublishStatusAsync(run, null, now, token);
            return result;
        }, cancellationToken);

    public Task<RunProgressResult> PauseAsync(PauseAgentRunCommand command, CancellationToken cancellationToken) =>
        MutateAsync(command.RunId, async (run, now, token) =>
        {
            var result = run.Pause(command.MessageId, command.Status, command.Reason, now);
            if (result == RunTransitionResult.Applied) await transport.PublishStatusAsync(run, null, now, token);
            return result;
        }, cancellationToken);

    public Task<AgentRunDto> ResumeAsync(ResumeAgentRunCommand command, CancellationToken cancellationToken) =>
        ResumeAfterPreflightAsync(command, cancellationToken);

    public async Task<AgentRunPreflightDto> PreflightAsync(
        PreflightAgentRunQuery query,
        CancellationToken cancellationToken)
    {
        var run = await runs.GetAsync(query.RunId, false, cancellationToken)
            ?? throw new AgentRunNotFoundException(query.RunId);
        if (!query.CanManageAll && run.OwnerUserId != query.RequestUserId)
            throw new AgentRunAccessDeniedException();
        if (run.Status is not (AgentRunStatus.PausedProviderUnavailable
            or AgentRunStatus.PausedInsufficientFunds) || string.IsNullOrWhiteSpace(run.ModelRoutesJson))
            return new(false, "RunNotPaused");
        var resumeSafety = await InspectResumeSafetyAsync(run, cancellationToken);
        if (!resumeSafety.Allowed)
            return new(false, resumeSafety.Code);
        var available = await routeResolver.PreflightAsync(
            run.OwnerUserId, run.ModelRoutesJson, cancellationToken);
        return new(available, available ? "Available" : "ProviderUnavailable");
    }

    private async Task<AgentRunDto> ResumeAfterPreflightAsync(
        ResumeAgentRunCommand command,
        CancellationToken cancellationToken)
    {
        var inspected = await runs.GetAsync(command.RunId, false, cancellationToken)
            ?? throw new AgentRunNotFoundException(command.RunId);
        if (!command.CanManageAll && inspected.OwnerUserId != command.RequestUserId)
            throw new AgentRunAccessDeniedException();
        if (inspected.ExecutionMessageId == command.OperationId
            && inspected.Status is AgentRunStatus.Queued or AgentRunStatus.Running)
            return ToDto(inspected);
        if (inspected.Status is not (AgentRunStatus.PausedProviderUnavailable
            or AgentRunStatus.PausedInsufficientFunds))
            throw new AgentRunConflictException("Only a paused run can resume.");
        var resumeSafety = await InspectResumeSafetyAsync(inspected, cancellationToken);
        if (!resumeSafety.Allowed)
            throw new AgentRunResumeBlockedException(
                resumeSafety.Code,
                "The previous provider outcome is not safe to resume.");
        if (string.IsNullOrWhiteSpace(inspected.ModelRoutesJson)
            || !await routeResolver.PreflightAsync(
                inspected.OwnerUserId, inspected.ModelRoutesJson, cancellationToken))
            throw new AgentRunConflictException(
                "Provider preflight must succeed before this run can resume.");
        var expectedRevision = inspected.Revision;
        return await unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var run = await runs.GetForUpdateAsync(command.RunId, token) ?? throw new AgentRunNotFoundException(command.RunId);
            if (!command.CanManageAll && run.OwnerUserId != command.RequestUserId) throw new AgentRunAccessDeniedException();
            if (run.ExecutionMessageId == command.OperationId
                && run.Status is AgentRunStatus.Queued or AgentRunStatus.Running)
                return ToDto(run);
            if (run.Revision != expectedRevision)
                throw new AgentRunConflictException("The run changed during provider preflight.");
            var messageId = command.OperationId; run.Resume(messageId);
            await transport.QueueAsync(run, messageId, token);
            await transport.PublishStatusAsync(run, null, timeProvider.GetUtcNow(), token);
            await unitOfWork.SaveChangesAsync(token); return ToDto(run);
        }, cancellationToken);
    }

    private async Task<AgentRunResumeSafetyDto> InspectResumeSafetyAsync(
        AgentRun run,
        CancellationToken cancellationToken)
    {
        var latestAttempt = run.Attempts.OrderByDescending(attempt => attempt.Sequence).FirstOrDefault();
        if (latestAttempt is null
            || !string.Equals(latestAttempt.Provider, "GitHubCopilotLocalCli", StringComparison.Ordinal))
            return new(true, "Allowed");
        return await routeResolver.InspectResumeSafetyAsync(
            run.OwnerUserId,
            latestAttempt.Provider,
            latestAttempt.IdempotencyKey,
            cancellationToken);
    }

    public Task<RunProgressResult> CompleteAsync(
        CompleteAgentRunCommand command,
        CancellationToken cancellationToken) =>
        MutateAsync(command.RunId, async (run, now, token) =>
        {
            var result = run.Complete(command.MessageId, command.Status, command.Result, command.Error, now);
            if (result == RunTransitionResult.Applied)
                await transport.PublishStatusAsync(run, null, now, token);
            return result;
        }, cancellationToken);

    public Task<RunProgressResult> FailAsync(
        FailAgentRunCommand command,
        CancellationToken cancellationToken) =>
        MutateAsync(command.RunId, async (run, now, token) =>
        {
            var result = run.FailBeforeOrDuringStart(command.MessageId, command.Error, now);
            if (result == RunTransitionResult.Applied)
                await transport.PublishStatusAsync(run, null, now, token);
            return result;
        }, cancellationToken);

    private Task<RunProgressResult> MutateAsync(
        Guid runId,
        Func<AgentRun, DateTimeOffset, CancellationToken, Task<RunTransitionResult>> mutation,
        CancellationToken cancellationToken)
    {
        if (runId == Guid.Empty) throw new ValidationException("A valid run id is required.");
        return unitOfWork.ExecuteInTransactionAsync(async token =>
        {
            var run = await runs.GetForUpdateAsync(runId, token)
                ?? throw new AgentRunNotFoundException(runId);
            try
            {
                var result = await mutation(run, timeProvider.GetUtcNow(), token);
                if (result == RunTransitionResult.Applied)
                    await unitOfWork.SaveChangesAsync(token);
                return new RunProgressResult(true, result == RunTransitionResult.AlreadyApplied, run.Revision, run.Status);
            }
            catch (AgentRunRuleViolationException exception)
            {
                throw new AgentRunConflictException(exception.Message);
            }
        }, cancellationToken);
    }

    private static AgentRunDto ToDto(AgentRun run) => new(
        run.Id, run.AgentId, run.OwnerUserId, run.CorrelationId,
        run.AgentName, run.AgentCode, run.AgentVersion,
        run.DirectionId, run.DirectionName, run.DirectionCode, run.DirectionVersion,
        run.ExecutionType, run.Input, run.ExpectedOutcome, run.Status, run.Revision,
        run.CreatedAt, run.StartedAt, run.CompletedAt, run.Result, run.Error,
        run.Steps.OrderBy(step => step.Sequence).Select(step => new RunStepDto(
            step.Id, step.Sequence, step.Name, step.Status, step.Log,
            step.StartedAt, step.UpdatedAt, step.CompletedAt)).ToArray(),
        run.Attempts.OrderBy(x => x.Sequence).Select(x => new AiProviderAttemptDto(x.Id, x.Sequence,
            x.Provider, x.Model, x.Status, x.FailureKind, x.ProviderRequestId, x.InputTokens,
            x.OutputTokens, x.EstimatedCostUsd, x.ActualModel, x.IdempotencyKey, x.RoutePriority,
            x.LatencyMilliseconds, x.CachedInputTokens, x.TotalTokens, x.UsageSource,
            x.CostSource, x.FinishStatus, x.FallbackReason, x.PartialOutput,
            x.StartedAt, x.CompletedAt, x.ErrorCode, x.Operation,
            x.GenerationDispatched, x.SafeExceptionType)).ToArray(),
        run.ModelRoutesJson, run.MaxAttempts, run.MaxEstimatedCostUsd,
        run.CheckpointJson, run.CheckpointVersion, run.CheckpointHash, run.CheckpointUpdatedAt);

    private static int Count(AgentRunOperationsData data, AgentRunStatus status) =>
        data.Counts.TryGetValue(status, out var count) ? count : 0;

    private static AgentRunOperationsItemDto ToOperationsDto(AgentRun run)
    {
        var activeStep = run.Steps
            .OrderByDescending(step => step.Sequence)
            .FirstOrDefault(step => step.Status == RunStepStatus.Running)
            ?? run.Steps.OrderByDescending(step => step.Sequence).FirstOrDefault();
        return new AgentRunOperationsItemDto(
            run.Id,
            run.AgentName,
            run.DirectionName,
            run.Status,
            run.Revision,
            run.CreatedAt,
            run.StartedAt,
            run.CompletedAt,
            activeStep?.Sequence,
            activeStep?.Name,
            activeStep?.Status);
    }
}
