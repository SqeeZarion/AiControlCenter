using System.Security.Cryptography;
using System.Text;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Orchestrator.Application;
using AiControlCenter.Orchestrator.Domain;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Orchestrator.Api.Grpc;

public sealed class RunProgressGrpcService(
    AgentRunApplicationService runs,
    IOptions<WorkerGrpcOptions> options) : RunProgress.RunProgressBase
{
    private const string ApiKeyHeader = "x-worker-api-key";

    public override async Task<RunProgressReply> BeginRun(BeginRunRequest request, ServerCallContext context)
    {
        Authenticate(context);
        var ids = ParseIds(request.RunId, request.MessageId);
        return Map(await ExecuteAsync(
            () => runs.BeginAsync(new BeginAgentRunCommand(ids.RunId, ids.MessageId), context.CancellationToken)));
    }

    public override async Task<RunProgressReply> ReportStep(ReportStepRequest request, ServerCallContext context)
    {
        Authenticate(context);
        var ids = ParseIds(request.RunId, request.MessageId);
        var status = request.Status switch
        {
            RunStepProgressStatus.Running => RunStepStatus.Running,
            RunStepProgressStatus.Succeeded => RunStepStatus.Succeeded,
            RunStepProgressStatus.Failed => RunStepStatus.Failed,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "A valid step status is required.")),
        };
        return Map(await ExecuteAsync(() => runs.ReportStepAsync(new ReportRunStepCommand(
            ids.RunId, ids.MessageId, request.Sequence, request.Name, status,
            string.IsNullOrWhiteSpace(request.Log) ? null : request.Log), context.CancellationToken)));
    }

    public override async Task<RunProgressReply> CompleteRun(CompleteRunRequest request, ServerCallContext context)
    {
        Authenticate(context);
        var ids = ParseIds(request.RunId, request.MessageId);
        var status = request.Status switch
        {
            RunCompletionStatus.Succeeded => AgentRunStatus.Succeeded,
            RunCompletionStatus.Failed => AgentRunStatus.Failed,
            _ => throw new RpcException(new Status(StatusCode.InvalidArgument, "A valid completion status is required.")),
        };
        return Map(await ExecuteAsync(() => runs.CompleteAsync(new CompleteAgentRunCommand(
            ids.RunId, ids.MessageId, status,
            string.IsNullOrWhiteSpace(request.Result) ? null : request.Result,
            string.IsNullOrWhiteSpace(request.Error) ? null : request.Error), context.CancellationToken)));
    }

    private static async Task<RunProgressResult> ExecuteAsync(Func<Task<RunProgressResult>> action)
    {
        try { return await action(); }
        catch (AgentRunNotFoundException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Run was not found."));
        }
        catch (AgentRunConflictException exception)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
        }
        catch (AgentRunRuleViolationException exception)
        {
            throw new RpcException(new Status(StatusCode.FailedPrecondition, exception.Message));
        }
    }

    private void Authenticate(ServerCallContext context)
    {
        var supplied = context.RequestHeaders.GetValue(ApiKeyHeader) ?? string.Empty;
        var expectedBytes = Encoding.UTF8.GetBytes(options.Value.ApiKey);
        var suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        if (expectedBytes.Length != suppliedBytes.Length
            || !CryptographicOperations.FixedTimeEquals(expectedBytes, suppliedBytes))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Worker authentication failed."));
    }

    private static (Guid RunId, Guid MessageId) ParseIds(string runId, string messageId)
    {
        if (!Guid.TryParse(runId, out var parsedRunId) || parsedRunId == Guid.Empty
            || !Guid.TryParse(messageId, out var parsedMessageId) || parsedMessageId == Guid.Empty)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Valid run and message ids are required."));
        return (parsedRunId, parsedMessageId);
    }

    private static RunProgressReply Map(RunProgressResult result) => new()
    {
        Accepted = result.Accepted,
        AlreadyApplied = result.AlreadyApplied,
        Revision = result.Revision,
        Status = result.Status.ToString(),
    };
}
