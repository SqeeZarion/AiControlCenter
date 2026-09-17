using AiControlCenter.Contracts.V1;
using AiControlCenter.Orchestrator.Application;
using AiControlCenter.Orchestrator.Domain;
using MassTransit;

namespace AiControlCenter.Orchestrator.Api.Messaging;

//відповідає за зв’язок Orchestrator із RabbitMQ через MassTransit.
public sealed class AgentRunTransport(IPublishEndpoint publishEndpoint) : IRunTransport
{
    //Метод викликається після створення AgentRun. Він формує команду: та публікує її через RabbitMQ.
    public Task QueueAsync(AgentRun run, Guid messageId, CancellationToken cancellationToken) =>
        publishEndpoint.Publish(new ExecuteTestAgentRunV1(
            messageId, run.Id, run.AgentId, run.AgentName, run.AgentCode, run.AgentVersion,
            run.DirectionId, run.DirectionName, run.DirectionCode, run.Input,
            run.ExpectedOutcome == TestRunOutcome.Fail ? TestRunOutcomeV1.Fail : TestRunOutcomeV1.Succeed,
            run.CorrelationId), context => context.MessageId = messageId, cancellationToken);

    //повідомити про новий статус
    public Task PublishStatusAsync(
        AgentRun run,
        RunStep? runStep,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken) =>
        publishEndpoint.Publish(new RunStatusChangedV1(
            Guid.NewGuid(), run.Id, run.OwnerUserId, run.Status.ToString(), run.Revision,
            occurredAt, runStep?.Sequence, runStep?.Status.ToString()), cancellationToken);
}

public sealed class AgentRunExecutionFaultConsumer(AgentRunApplicationService runs)
    : IConsumer<Fault<ExecuteTestAgentRunV1>>
{
    public Task Consume(ConsumeContext<Fault<ExecuteTestAgentRunV1>> context) =>
        runs.FailAsync(new FailAgentRunCommand(
            context.Message.Message.RunId,
            context.Message.Message.MessageId,
            "Worker could not complete the deterministic test run after bounded retries."),
            context.CancellationToken);
}
