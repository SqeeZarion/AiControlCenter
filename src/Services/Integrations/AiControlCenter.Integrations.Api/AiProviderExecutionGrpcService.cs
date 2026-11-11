using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Integrations.Application;
using Grpc.Core;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Integrations.Api;

public sealed class AiProviderExecutionGrpcService(ProviderConnectionService providers,
    IOptions<InternalGrpcOptions> options) : AiProviderExecution.AiProviderExecutionBase
{
    public override async Task<InspectResumeSafetyReply> InspectResumeSafety(
        InspectResumeSafetyRequest request,
        ServerCallContext context)
    {
        Authenticate(context);
        if (!Guid.TryParse(request.OwnerUserId, out var owner) || owner == Guid.Empty
            || !Enum.TryParse<AiControlCenter.Integrations.Domain.AiProviderKind>(
                request.Provider, false, out var provider)
            || !Enum.IsDefined(provider))
            throw new RpcException(new Status(
                StatusCode.InvalidArgument, "A valid owner id and provider are required."));
        try
        {
            var result = await providers.InspectResumeSafetyAsync(
                owner, provider, request.IdempotencyKey, context.CancellationToken);
            return new InspectResumeSafetyReply { Allowed = result.Allowed, Code = result.Code };
        }
        catch (ArgumentException exception)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
        }
    }

    public override async Task<ResolveAiProviderRoutesReply> ResolveRoutes(
        ResolveAiProviderRoutesRequest request,
        ServerCallContext context)
    {
        Authenticate(context);
        if (!Guid.TryParse(request.OwnerUserId, out var owner) || owner == Guid.Empty)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A valid owner id is required."));
        try
        {
            var result = await providers.ResolveRoutesAsync(owner, request.LogicalRoutesJson, context.CancellationToken);
            return new ResolveAiProviderRoutesReply
            {
                ResolvedRoutesJson = result.Json,
                AllAvailable = result.AllAvailable,
            };
        }
        catch (ArgumentException exception)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, exception.Message));
        }
        catch (System.Text.Json.JsonException)
        {
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Logical routes JSON is malformed."));
        }
    }

    public override async Task<ExecuteAiProviderReply> Execute(ExecuteAiProviderRequest request, ServerCallContext context)
    {
        Authenticate(context);
        if (!Guid.TryParse(request.OwnerUserId, out var owner) || owner == Guid.Empty
            || !Guid.TryParse(request.ConnectionId, out var connection) || connection == Guid.Empty)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Valid owner and connection ids are required."));
        decimal? dailyCap = decimal.TryParse(request.DailyCostCapUsd, NumberStyles.Number,
            CultureInfo.InvariantCulture, out var daily) ? daily : null;
        decimal? monthlyCap = decimal.TryParse(request.MonthlyCostCapUsd, NumberStyles.Number,
            CultureInfo.InvariantCulture, out var monthly) ? monthly : null;
        decimal? runCap = decimal.TryParse(request.RunCostCapUsd, NumberStyles.Number,
            CultureInfo.InvariantCulture, out var run) ? run : null;
        decimal? routeCap = decimal.TryParse(request.RouteCostCapUsd, NumberStyles.Number,
            CultureInfo.InvariantCulture, out var route) ? route : null;
        AiControlCenter.Integrations.Domain.AiProviderKind? expectedProvider =
            Enum.TryParse<AiControlCenter.Integrations.Domain.AiProviderKind>(request.ExpectedProvider, false, out var expected)
            && Enum.IsDefined(expected) ? expected : null;
        if (expectedProvider is null)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "Expected provider is required."));
        var result = await providers.ExecuteAsync(new AiExecutionRequest(owner, connection, request.ModelId,
            request.SystemPrompt, request.Input, request.AllowedTools,
            string.IsNullOrWhiteSpace(request.Scenario) ? null : request.Scenario, request.IdempotencyKey,
            dailyCap, monthlyCap,
            request.MaxRequestsPerDay > 0 ? request.MaxRequestsPerDay : null,
            request.MaxRequestsPerMonth > 0 ? request.MaxRequestsPerMonth : null,
            request.MaxInputTokens > 0 ? request.MaxInputTokens : null,
            request.MaxOutputTokens > 0 ? request.MaxOutputTokens : null,
            runCap, routeCap, expectedProvider,
            PriorToolResults: request.PriorToolResults.Select(MapToolInvocation).ToArray(),
            MaxToolCycles: request.MaxToolCycles > 0 ? request.MaxToolCycles : 4,
            CheckpointToolAuditBudgetBytes: request.CheckpointToolAuditBudgetBytes),
            context.CancellationToken);
        var reply = new ExecuteAiProviderReply
        {
            Succeeded = result.Succeeded,
            Output = result.Output ?? string.Empty,
            FailureKind = result.FailureKind.ToString(),
            ErrorCode = result.ErrorCode ?? string.Empty,
            ProviderRequestId = result.ProviderRequestId ?? string.Empty,
            InputTokens = result.InputTokens,
            OutputTokens = result.OutputTokens,
            EstimatedCostUsd = result.EstimatedCostUsd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            CanFallback = ProviderFallbackPolicy.CanFallback(result.FailureKind),
            ActualModel = result.ActualModel ?? string.Empty,
            LatencyMilliseconds = result.LatencyMilliseconds,
            UsageSource = result.UsageSource.ToString(),
            CostSource = result.CostSource.ToString(),
            FinishStatus = result.FinishStatus,
            PartialOutput = result.PartialOutput ?? string.Empty,
            Operation = result.Operation ?? string.Empty,
            GenerationDispatched = result.GenerationDispatched,
            SafeExceptionType = result.SafeExceptionType ?? string.Empty,
            OperationDiagnosticsJson = JsonSerializer.Serialize(result.OperationDiagnostics ?? []),
        };
        if (result.CachedInputTokens is int cachedInputTokens)
            reply.CachedInputTokens = cachedInputTokens;
        reply.ToolInvocations.AddRange(result.ToolInvocations.Select(MapToolInvocation));
        return reply;
    }

    private static ToolInvocationResult MapToolInvocation(ToolInvocationMessage value) => new(
        value.ToolName, value.Succeeded,
        string.IsNullOrEmpty(value.Output) ? null : value.Output,
        string.IsNullOrEmpty(value.Error) ? null : value.Error,
        value.CallId, value.ArgumentsJson, value.Status,
        DateTimeOffset.TryParse(value.StartedAt, out var started) ? started : null,
        DateTimeOffset.TryParse(value.CompletedAt, out var completed) ? completed : null,
        value.IdempotencyKey);

    private static ToolInvocationMessage MapToolInvocation(ToolInvocationResult value) => new()
    {
        CallId = value.CallId,
        ToolName = value.ToolName,
        ArgumentsJson = value.ArgumentsJson,
        Succeeded = value.Succeeded,
        Output = value.Output ?? string.Empty,
        Error = value.Error ?? string.Empty,
        Status = value.Status,
        StartedAt = value.StartedAt?.ToString("O") ?? string.Empty,
        CompletedAt = value.CompletedAt?.ToString("O") ?? string.Empty,
        IdempotencyKey = value.IdempotencyKey,
    };
    private void Authenticate(ServerCallContext context)
    {
        var supplied = Encoding.UTF8.GetBytes(context.RequestHeaders.GetValue("x-worker-api-key") ?? string.Empty);
        var expected = Encoding.UTF8.GetBytes(options.Value.ApiKey);
        if (supplied.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(supplied, expected))
            throw new RpcException(new Status(StatusCode.Unauthenticated, "Worker authentication failed."));
    }
}
