using AiControlCenter.Orchestrator.Domain;
using FluentValidation;

namespace AiControlCenter.Orchestrator.Application;

public sealed class AgentRunApplicationService(
    IAgentRunRepository runs,
    IOrchestratorUnitOfWork unitOfWork,
    IAgentCatalogClient agentCatalog,
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
        var run = AgentRun.Create(
            Guid.NewGuid(), command.AgentId, command.OwnerUserId, command.CorrelationId,
            snapshot, command.Input, command.ExpectedOutcome, timeProvider.GetUtcNow());
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
            step.StartedAt, step.UpdatedAt, step.CompletedAt)).ToArray());
}
