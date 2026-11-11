using System.Text.Json;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Orchestrator.Application;
using Grpc.Core;
using Microsoft.Extensions.Configuration;

namespace AiControlCenter.Orchestrator.Api.Grpc;

public sealed class AiProviderRouteResolver(
    AiProviderExecution.AiProviderExecutionClient client,
    IConfiguration configuration) : IAiProviderRouteResolver
{
    public async Task<string> ResolveAsync(
        Guid ownerUserId,
        string logicalRoutesJson,
        CancellationToken cancellationToken)
    {
        var apiKey = configuration["WorkerGrpc:ApiKey"]
            ?? throw new InvalidOperationException("WorkerGrpc:ApiKey is required.");
        try
        {
            var reply = await client.ResolveRoutesAsync(new ResolveAiProviderRoutesRequest
            {
                OwnerUserId = ownerUserId.ToString(),
                LogicalRoutesJson = logicalRoutesJson,
            }, new Metadata { { "x-worker-api-key", apiKey } }, cancellationToken: cancellationToken);
            return reply.ResolvedRoutesJson;
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.InvalidArgument)
        {
            throw new ArgumentException("Persisted AI provider routes are invalid.",
                nameof(logicalRoutesJson), exception);
        }
    }

    public async Task<bool> PreflightAsync(
        Guid ownerUserId,
        string resolvedRoutesJson,
        CancellationToken cancellationToken)
    {
        using var document = JsonDocument.Parse(resolvedRoutesJson);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
            return false;
        var logicalRoutes = document.RootElement.EnumerateArray().Select(route => new Dictionary<string, object?>
        {
            ["provider"] = route.GetProperty("provider").GetString(),
            ["connectionId"] = route.GetProperty("connectionId").GetString(),
            ["modelId"] = route.GetProperty("modelId").GetString(),
            ["priority"] = route.GetProperty("priority").GetInt32(),
            ["enabled"] = route.TryGetProperty("enabled", out var enabled) ? enabled.GetBoolean() : true,
            ["maxAttempts"] = route.TryGetProperty("maxAttempts", out var attempts) ? attempts.GetInt32() : 1,
            ["timeoutSeconds"] = route.TryGetProperty("timeoutSeconds", out var timeout) ? timeout.GetInt32() : 60,
            ["paidExecutionAllowed"] = route.TryGetProperty("paidExecutionAllowed", out var paid) && paid.GetBoolean(),
            ["paidFallbackAllowed"] = route.TryGetProperty("paidFallbackAllowed", out var fallback) && fallback.GetBoolean(),
        }).ToArray();
        var apiKey = configuration["WorkerGrpc:ApiKey"]
            ?? throw new InvalidOperationException("WorkerGrpc:ApiKey is required.");
        var reply = await client.ResolveRoutesAsync(new ResolveAiProviderRoutesRequest
        {
            OwnerUserId = ownerUserId.ToString(),
            LogicalRoutesJson = JsonSerializer.Serialize(logicalRoutes),
        }, new Metadata { { "x-worker-api-key", apiKey } }, cancellationToken: cancellationToken);
        return reply.AllAvailable;
    }

    public async Task<AgentRunResumeSafetyDto> InspectResumeSafetyAsync(
        Guid ownerUserId,
        string provider,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var apiKey = configuration["WorkerGrpc:ApiKey"]
            ?? throw new InvalidOperationException("WorkerGrpc:ApiKey is required.");
        try
        {
            var reply = await client.InspectResumeSafetyAsync(new InspectResumeSafetyRequest
            {
                OwnerUserId = ownerUserId.ToString(),
                Provider = provider,
                IdempotencyKey = idempotencyKey,
            }, new Metadata { { "x-worker-api-key", apiKey } }, cancellationToken: cancellationToken);
            return new(reply.Allowed, reply.Code);
        }
        catch (RpcException)
        {
            return new(false, "ResumeSafetyUnverifiable");
        }
    }
}
