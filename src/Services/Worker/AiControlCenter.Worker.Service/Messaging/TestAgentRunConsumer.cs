using AiControlCenter.Contracts.V1;
using AiControlCenter.Grpc.Contracts.V1;
using Grpc.Core;
using MassTransit;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Worker.Service.Messaging;

public sealed partial class TestAgentRunConsumer(
    RunProgress.RunProgressClient progress,
    IOptions<WorkerExecutionOptions> options,
    TimeProvider timeProvider,
    ILogger<TestAgentRunConsumer> logger) : IConsumer<ExecuteTestAgentRunV1>
{
    public async Task Consume(ConsumeContext<ExecuteTestAgentRunV1> context)
    {
        var job = context.Message;
        var begin = await progress.BeginRunAsync(
            new BeginRunRequest { RunId = job.RunId.ToString(), MessageId = job.MessageId.ToString() },
            Headers(), Deadline(), context.CancellationToken);
        if (begin.Status is "Succeeded" or "Failed")
        {
            LogDuplicateTerminal(logger, job.RunId, begin.Status);
            return;
        }

        await Step(job, 1, "Перевірка вводу", "Вхідні дані перевірено.", context.CancellationToken);
        await Report(job, 2, "Тестова дія", RunStepProgressStatus.Running,
            "Детермінована тестова дія розпочата.", context.CancellationToken);
        if (job.ExpectedOutcome == TestRunOutcomeV1.Fail)
        {
            await Report(job, 2, "Тестова дія", RunStepProgressStatus.Failed,
                "Тестовий сценарій завершено контрольованою помилкою.", context.CancellationToken);
            await Complete(job, RunCompletionStatus.Failed, null,
                "Requested deterministic failure was completed safely.", context.CancellationToken);
            return;
        }

        await Report(job, 2, "Тестова дія", RunStepProgressStatus.Succeeded,
            "Детерміновану тестову дію виконано.", context.CancellationToken);
        await Step(job, 3, "Завершення", "Результат підготовлено.", context.CancellationToken);
        await Complete(job, RunCompletionStatus.Succeeded,
            $"Test agent '{job.AgentName}' completed successfully.", null, context.CancellationToken);
    }

    private async Task Step(
        ExecuteTestAgentRunV1 job,
        int sequence,
        string name,
        string log,
        CancellationToken cancellationToken)
    {
        await Report(job, sequence, name, RunStepProgressStatus.Running, log, cancellationToken);
        await Report(job, sequence, name, RunStepProgressStatus.Succeeded, log, cancellationToken);
    }

    private async Task Report(
        ExecuteTestAgentRunV1 job,
        int sequence,
        string name,
        RunStepProgressStatus status,
        string log,
        CancellationToken cancellationToken) =>
        _ = await progress.ReportStepAsync(new ReportStepRequest
        {
            RunId = job.RunId.ToString(),
            MessageId = job.MessageId.ToString(),
            Sequence = sequence,
            Name = name,
            Status = status,
            Log = log,
        }, Headers(), Deadline(), cancellationToken);

    private async Task Complete(
        ExecuteTestAgentRunV1 job,
        RunCompletionStatus status,
        string? result,
        string? error,
        CancellationToken cancellationToken) =>
        _ = await progress.CompleteRunAsync(new CompleteRunRequest
        {
            RunId = job.RunId.ToString(),
            MessageId = job.MessageId.ToString(),
            Status = status,
            Result = result ?? string.Empty,
            Error = error ?? string.Empty,
        }, Headers(), Deadline(), cancellationToken);

    private Metadata Headers() => new() { { "x-worker-api-key", options.Value.ApiKey } };
    private DateTime Deadline() => timeProvider.GetUtcNow().UtcDateTime.AddSeconds(options.Value.DeadlineSeconds);

    [LoggerMessage(EventId = 4200, Level = LogLevel.Information,
        Message = "Ignoring redelivery for terminal run {RunId} with status {Status}")]
    private static partial void LogDuplicateTerminal(ILogger logger, Guid runId, string status);
}
