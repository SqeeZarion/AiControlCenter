using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using AiControlCenter.Contracts.V1;
using AiControlCenter.Grpc.Contracts.V1;
using Grpc.Core;
using MassTransit;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Worker.Service.Messaging;

public sealed class AiAgentRunConsumer(RunProgress.RunProgressClient progress,
    AiProviderExecution.AiProviderExecutionClient providers,
    IOptions<WorkerExecutionOptions> orchestratorOptions,
    IOptions<IntegrationsExecutionOptions> integrationsOptions,
    TimeProvider timeProvider) : IConsumer<ExecuteAiAgentRunV1>
{
    private static readonly JsonSerializerOptions ModelRouteSerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public async Task Consume(ConsumeContext<ExecuteAiAgentRunV1> context)
    {
        var job = context.Message;
        var begin = await progress.BeginRunAsync(new BeginRunRequest { RunId = job.RunId.ToString(), MessageId = job.MessageId.ToString() },
            OrchestratorHeaders(), OrchestratorDeadline(), context.CancellationToken);
        if (begin.Status is "Succeeded" or "Failed" or "Cancelled"
            or "PausedProviderUnavailable" or "PausedInsufficientFunds") return;
        await ReportStep(job, RunStepProgressStatus.Running, "AI provider execution started.", context.CancellationToken);
        var routes = JsonSerializer.Deserialize<ModelRoute[]>(job.ModelRoutesJson, ModelRouteSerializerOptions)
            ?.Where(x => x.Enabled).OrderBy(x => x.Priority).ToArray() ?? [];
        var configuredPlans = routes.SelectMany(route => Enumerable.Repeat(route, Math.Clamp(route.MaxAttempts, 1, 3)))
            .Take(job.MaxAttempts).ToArray();
        var plans = ExecutionPlan.Build(configuredPlans, job.MaxAttempts, job.AttemptOffset);
        var tools = JsonSerializer.Deserialize<string[]>(job.ToolPolicyJson) ?? [];
        var checkpoint = ReadCheckpoint(job.CheckpointJson);
        var completedTools = checkpoint.Tools.ToDictionary(tool => tool.CallId, StringComparer.Ordinal);
        var attemptReferences = checkpoint.Attempts.ToList();
        ExecuteAiProviderReply? last = null;
        var attempt = job.AttemptOffset;
        var accumulatedEstimatedCost = job.AccumulatedEstimatedCostUsd;
        foreach (var route in plans)
        {
            attempt++;
            if (route.OwnerUserId != job.OwnerUserId)
            {
                await Fail(job, "Resolved provider route owner does not match the run owner.", context.CancellationToken);
                return;
            }
            if (!route.ConnectionAvailable || route.ConnectionId == Guid.Empty)
            {
                _ = await progress.PauseRunAsync(new PauseRunRequest
                {
                    RunId = job.RunId.ToString(),
                    MessageId = job.MessageId.ToString(),
                    Status = "PausedProviderUnavailable",
                    Reason = "The run owner has no active connection for a required provider route.",
                }, OrchestratorHeaders(), OrchestratorDeadline(), context.CancellationToken);
                return;
            }
            var isPaid = route.IsPaid;
            if (isPaid && !route.PricingKnown)
            {
                await Fail(job, "Paid provider execution was blocked because model pricing is unknown.", context.CancellationToken);
                return;
            }
            if (isPaid && (!route.PaidExecutionAllowed || route.SafetyCapUsd is null
                || route.SafetyCapUsd <= 0 || route.SafetyCapUsd > job.MaxEstimatedCostUsd))
            {
                await Fail(job, "Paid provider execution requires explicit permission and a bounded safety cap.", context.CancellationToken);
                return;
            }
            if (isPaid && attempt > 1 && !route.PaidFallbackAllowed)
            {
                await Fail(job, "Automatic paid fallback is disabled for this route.", context.CancellationToken);
                return;
            }
            if (isPaid && accumulatedEstimatedCost >= job.MaxEstimatedCostUsd)
            {
                await Fail(job, "The run cost cap was reached before the next paid provider attempt.", context.CancellationToken);
                return;
            }
            using var attemptTimeout = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            attemptTimeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(route.TimeoutSeconds, 1, 300)));
            var checkpointTimestamp = timeProvider.GetUtcNow();
            var checkpointToolAuditBudget = CheckpointEnvelopeBudget.CalculateToolAuditBudget(
                job.Input,
                job.ModelRoutesJson,
                JsonSerializer.Serialize(attemptReferences, ModelRouteSerializerOptions),
                checkpoint.Version + 1,
                attempt,
                checkpointTimestamp);
            if (checkpointToolAuditBudget <= 0)
            {
                await Fail(job, "AI checkpoint has no safe capacity for another provider attempt.",
                    context.CancellationToken);
                return;
            }
            try
            {
                var idempotencyKey = $"{job.RunId:N}:{attempt}:{job.MessageId:N}";
                var executionRequest = new ExecuteAiProviderRequest
                {
                    OwnerUserId = job.OwnerUserId.ToString(),
                    ConnectionId = route.ConnectionId.ToString(),
                    ModelId = route.ModelId,
                    SystemPrompt = job.SystemPrompt,
                    Input = job.Input,
                    Scenario = route.Scenario ?? string.Empty,
                    IdempotencyKey = idempotencyKey,
                    DailyCostCapUsd = route.DailyCostCapUsd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    MonthlyCostCapUsd = route.MonthlyCostCapUsd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    MaxRequestsPerDay = route.MaxRequestsPerDay ?? 0,
                    MaxRequestsPerMonth = route.MaxRequestsPerMonth ?? 0,
                    MaxInputTokens = job.MaxInputTokens,
                    MaxOutputTokens = job.MaxOutputTokens,
                    RunCostCapUsd = Math.Max(0, job.MaxEstimatedCostUsd - accumulatedEstimatedCost)
                        .ToString(CultureInfo.InvariantCulture),
                    RouteCostCapUsd = route.SafetyCapUsd?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                    ExpectedProvider = route.Provider,
                    MaxToolCycles = 4,
                    CheckpointToolAuditBudgetBytes = checkpointToolAuditBudget,
                    AllowedTools = { tools },
                };
                executionRequest.PriorToolResults.AddRange(completedTools.Values.Select(MapToolInvocation));
                last = await providers.ExecuteAsync(executionRequest,
                    IntegrationsHeaders(), IntegrationsDeadline(route.TimeoutSeconds), attemptTimeout.Token);
            }
            catch (RpcException exception) when (exception.StatusCode is StatusCode.DeadlineExceeded or StatusCode.Cancelled
                && !context.CancellationToken.IsCancellationRequested)
            {
                last = new ExecuteAiProviderReply
                {
                    FailureKind = "AmbiguousTimeout",
                    ErrorCode = "provider_attempt_timeout",
                    CanFallback = false,
                    UsageSource = "Unavailable",
                    CostSource = "Unavailable",
                    FinishStatus = "Unknown",
                    Operation = "Unknown",
                    GenerationDispatched = "unknown",
                    SafeExceptionType = nameof(RpcException),
                };
            }
            last.ProviderRequestId = ProviderCheckpointMetadataContract.CanonicalizeProviderRequestId(
                last.ProviderRequestId) ?? string.Empty;
            last.ActualModel = ProviderCheckpointMetadataContract.CanonicalizeActualModel(last.ActualModel)
                ?? string.Empty;
            last.FailureKind = ProviderCheckpointMetadataContract.CanonicalizeFailureReason(last.FailureKind)
                ?? string.Empty;
            var attemptStatus = ProviderCheckpointMetadataContract.CanonicalizeAttemptStatus(
                last.Succeeded ? "Succeeded" : last.FailureKind) ?? "Unknown";
            var attemptReport = new ReportAiAttemptRequest
            {
                RunId = job.RunId.ToString(),
                MessageId = job.MessageId.ToString(),
                Sequence = attempt,
                Provider = route.Provider,
                Model = route.ModelId,
                Status = last.Succeeded ? "Succeeded" : "Failed",
                FailureKind = last.FailureKind,
                ProviderRequestId = last.ProviderRequestId,
                InputTokens = last.InputTokens,
                OutputTokens = last.OutputTokens,
                EstimatedCostUsd = last.EstimatedCostUsd,
                ActualModel = last.ActualModel ?? string.Empty,
                IdempotencyKey = $"{job.RunId:N}:{attempt}:{job.MessageId:N}",
                RoutePriority = route.Priority,
                LatencyMilliseconds = last.LatencyMilliseconds,
                UsageSource = last.UsageSource,
                CostSource = last.CostSource,
                FinishStatus = last.FinishStatus,
                FallbackReason = last.Succeeded ? string.Empty : last.FailureKind,
                PartialOutput = last.PartialOutput ?? string.Empty,
                ErrorCode = ProviderCheckpointMetadataContract.CanonicalizeErrorCode(last.ErrorCode)
                    ?? string.Empty,
                Operation = ProviderCheckpointMetadataContract.CanonicalizeOperation(last.Operation)
                    ?? string.Empty,
                GenerationDispatched = ProviderCheckpointMetadataContract.CanonicalizeGenerationDispatched(
                    last.GenerationDispatched),
                SafeExceptionType = ProviderCheckpointMetadataContract.CanonicalizeSafeExceptionType(
                    last.SafeExceptionType) ?? string.Empty,
            };
            if (last.HasCachedInputTokens)
                attemptReport.CachedInputTokens = last.CachedInputTokens;
            foreach (var tool in last.ToolInvocations)
            {
                completedTools[tool.CallId] = ReadToolInvocation(tool);
                attemptReport.ToolInvocations.Add(tool);
            }
            attemptReferences.RemoveAll(reference => reference.Sequence == attempt);
            attemptReferences.Add(new AttemptCheckpoint(attempt, last.ProviderRequestId,
                last.ActualModel ?? string.Empty, attemptStatus));
            if (!TryCreateCheckpoint(job, checkpoint.Version + 1,
                attempt, last, completedTools.Values, attemptReferences, checkpointTimestamp,
                out var checkpointJson))
            {
                await Fail(job, "AI checkpoint exceeded the bounded persistence limit.",
                    context.CancellationToken);
                return;
            }
            attemptReport.CheckpointJson = checkpointJson;
            checkpoint = checkpoint with { Version = checkpoint.Version + 1 };
            await progress.ReportAttemptAsync(attemptReport,
                OrchestratorHeaders(), OrchestratorDeadline(), context.CancellationToken);
            var attemptCost = decimal.TryParse(last.EstimatedCostUsd, NumberStyles.Number,
                CultureInfo.InvariantCulture, out var parsedCost) ? parsedCost : 0;
            accumulatedEstimatedCost += attemptCost;
            if (isPaid && (attemptCost > route.SafetyCapUsd || accumulatedEstimatedCost > job.MaxEstimatedCostUsd))
            {
                await Fail(job, "AI execution exceeded the configured paid-provider safety cap.", context.CancellationToken);
                return;
            }
            if (last.Succeeded)
            {
                if (last.InputTokens > job.MaxInputTokens || last.OutputTokens > job.MaxOutputTokens
                    || accumulatedEstimatedCost > job.MaxEstimatedCostUsd)
                { await Fail(job, "AI execution exceeded the configured run budget.", context.CancellationToken); return; }
                await ReportStep(job, RunStepProgressStatus.Succeeded, "AI provider execution completed.", context.CancellationToken);
                await Complete(job, RunCompletionStatus.Succeeded, last.Output, null, context.CancellationToken); return;
            }
            if (last.FailureKind == "AmbiguousTimeout"
                || last.GenerationDispatched == "unknown")
            {
                const string ambiguousReason =
                    "The provider request outcome is unknown. Automatic retry, fallback, and Resume are blocked.";
                await ReportStep(job, RunStepProgressStatus.Failed, ambiguousReason,
                    context.CancellationToken);
                await Fail(job, ambiguousReason, context.CancellationToken);
                return;
            }
            var fallbackAllowed = last.CanFallback
                && (route.AllowedFallbackReasons ?? []).Contains(last.FailureKind, StringComparer.Ordinal);
            if (!fallbackAllowed)
            {
                if (last.FailureKind is "Authentication" or "QuotaExceeded" or "RateLimited"
                    or "TemporarilyUnavailable" or "ModelUnavailable" or "InsufficientFunds")
                {
                    _ = await progress.PauseRunAsync(new PauseRunRequest
                    {
                        RunId = job.RunId.ToString(),
                        MessageId = job.MessageId.ToString(),
                        Status = last.FailureKind == "InsufficientFunds"
                            ? "PausedInsufficientFunds" : "PausedProviderUnavailable",
                        Reason = last.FailureKind == "Authentication"
                            ? "The provider connection requires authentication recovery."
                            : "The configured provider route is currently unavailable.",
                    }, OrchestratorHeaders(), OrchestratorDeadline(), context.CancellationToken);
                    return;
                }
                var reason = BuildTerminalFailureReason(last);
                await ReportStep(job, RunStepProgressStatus.Failed, reason, context.CancellationToken);
                await Fail(job, reason, context.CancellationToken);
                return;
            }
        }
        var paused = last?.FailureKind == "InsufficientFunds" ? "PausedInsufficientFunds" : "PausedProviderUnavailable";
        _ = await progress.PauseRunAsync(new PauseRunRequest
        {
            RunId = job.RunId.ToString(),
            MessageId = job.MessageId.ToString(),
            Status = paused,
            Reason = "No configured model route is currently available."
        },
            OrchestratorHeaders(), OrchestratorDeadline(), context.CancellationToken);
    }

    private async Task ReportStep(ExecuteAiAgentRunV1 job, RunStepProgressStatus status, string log, CancellationToken token) =>
        _ = await progress.ReportStepAsync(new ReportStepRequest
        {
            RunId = job.RunId.ToString(),
            MessageId = job.MessageId.ToString(),
            Sequence = 1,
            Name = "AI execution",
            Status = status,
            Log = log
        }, OrchestratorHeaders(), OrchestratorDeadline(), token);
    private Task Fail(ExecuteAiAgentRunV1 job, string error, CancellationToken token) => Complete(job, RunCompletionStatus.Failed, null, error, token);
    private static string BuildTerminalFailureReason(ExecuteAiProviderReply reply)
    {
        var operation = ProviderCheckpointMetadataContract.CanonicalizeOperation(reply.Operation);
        var code = ProviderCheckpointMetadataContract.CanonicalizeErrorCode(reply.ErrorCode);
        var diagnostic = operation is null && code is null
            ? string.Empty
            : $" during {operation ?? "Unknown"} [{code ?? "unknown"}]";
        return $"AI provider rejected the run ({reply.FailureKind}){diagnostic}; fallback is not permitted.";
    }
    private async Task Complete(ExecuteAiAgentRunV1 job, RunCompletionStatus status, string? result, string? error, CancellationToken token) =>
        _ = await progress.CompleteRunAsync(new CompleteRunRequest
        {
            RunId = job.RunId.ToString(),
            MessageId = job.MessageId.ToString(),
            Status = status,
            Result = result ?? string.Empty,
            Error = error ?? string.Empty
        }, OrchestratorHeaders(), OrchestratorDeadline(), token);
    private Metadata OrchestratorHeaders() => new() { { "x-worker-api-key", orchestratorOptions.Value.ApiKey } };
    private Metadata IntegrationsHeaders() => new() { { "x-worker-api-key", integrationsOptions.Value.ApiKey } };
    private DateTime OrchestratorDeadline() => timeProvider.GetUtcNow().UtcDateTime.AddSeconds(orchestratorOptions.Value.DeadlineSeconds);
    private DateTime IntegrationsDeadline(int routeTimeoutSeconds) => timeProvider.GetUtcNow().UtcDateTime.AddSeconds(
        Math.Min(integrationsOptions.Value.DeadlineSeconds, Math.Clamp(routeTimeoutSeconds, 1, 300)));

    private static CheckpointState ReadCheckpoint(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new(0, [], []);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var version = root.TryGetProperty("checkpointVersion", out var versionNode) ? versionNode.GetInt32() : 0;
        var tools = root.TryGetProperty("completedToolCalls", out var toolsNode)
            ? toolsNode.Deserialize<ToolCheckpoint[]>(ModelRouteSerializerOptions) ?? [] : [];
        var attempts = root.TryGetProperty("providerAttemptReferences", out var attemptsNode)
            ? attemptsNode.Deserialize<AttemptCheckpoint[]>(ModelRouteSerializerOptions) ?? [] : [];
        return new(version, tools, attempts);
    }

    private static bool TryCreateCheckpoint(ExecuteAiAgentRunV1 job, int version, int attempt,
        ExecuteAiProviderReply result, IEnumerable<ToolCheckpoint> tools,
        IReadOnlyCollection<AttemptCheckpoint> attempts, DateTimeOffset timestamp,
        out string checkpointJson)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["checkpointVersion"] = version,
            ["logicalStepId"] = "ai-execution",
            ["confirmedContext"] = job.Input,
            ["partialOutput"] = result.PartialOutput,
            ["partialOutputIsComplete"] = false,
            ["completedToolCalls"] = JsonSerializer.SerializeToNode(tools),
            ["providerAttemptReferences"] = JsonSerializer.SerializeToNode(attempts),
            ["resumeCursor"] = attempt,
            ["lastSafeTimestamp"] = timestamp.ToString("O"),
            ["routeSnapshot"] = JsonNode.Parse(job.ModelRoutesJson),
        };
        var payload = root.ToJsonString();
        root["hash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        checkpointJson = root.ToJsonString();
        return Encoding.UTF8.GetByteCount(checkpointJson)
            <= CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes;
    }

    private static ToolCheckpoint ReadToolInvocation(ToolInvocationMessage value) => new(
        value.CallId, value.ToolName, value.ArgumentsJson, value.Succeeded,
        string.IsNullOrEmpty(value.Output) ? null : value.Output,
        string.IsNullOrEmpty(value.Error) ? null : value.Error,
        value.Status, value.StartedAt, value.CompletedAt, value.IdempotencyKey);

    private static ToolInvocationMessage MapToolInvocation(ToolCheckpoint value) => new()
    {
        CallId = value.CallId,
        ToolName = value.ToolName,
        ArgumentsJson = value.ArgumentsJson,
        Succeeded = value.Succeeded,
        Output = value.Output ?? string.Empty,
        Error = value.Error ?? string.Empty,
        Status = value.Status,
        StartedAt = value.StartedAt,
        CompletedAt = value.CompletedAt,
        IdempotencyKey = value.IdempotencyKey,
    };

    private sealed record CheckpointState(int Version, ToolCheckpoint[] Tools, AttemptCheckpoint[] Attempts);
    private sealed record ToolCheckpoint(string CallId, string ToolName, string ArgumentsJson,
        bool Succeeded, string? Output, string? Error, string Status, string StartedAt,
        string CompletedAt, string IdempotencyKey);
    private sealed record AttemptCheckpoint(int Sequence, string ProviderRequestId, string ActualModel, string Status);
    private sealed record ModelRoute(
        Guid OwnerUserId,
        Guid ConnectionId,
        string Provider,
        string ModelId,
        int Priority,
        bool Enabled = true,
        string? Scenario = null,
        string[]? AllowedFallbackReasons = null,
        int MaxAttempts = 1,
        int TimeoutSeconds = 60,
        bool PaidExecutionAllowed = false,
        bool PaidFallbackAllowed = false,
        decimal? SafetyCapUsd = null,
        decimal? DailyCostCapUsd = null,
        decimal? MonthlyCostCapUsd = null,
        int? MaxRequestsPerDay = null,
        int? MaxRequestsPerMonth = null,
        bool ConnectionAvailable = false,
        bool IsPaid = false,
        bool PricingKnown = false);
}

internal static class ExecutionPlan
{
    public static IReadOnlyList<T> Build<T>(IReadOnlyList<T> configuredPlans, int maxAttempts, int attemptOffset)
    {
        var remainingAttempts = Math.Max(0, maxAttempts - attemptOffset);
        if (remainingAttempts == 0 || configuredPlans.Count == 0) return [];
        if (attemptOffset == 0) return configuredPlans.Take(remainingAttempts).ToArray();

        return Enumerable.Range(0, remainingAttempts)
            .Select(index => configuredPlans[index % configuredPlans.Count])
            .ToArray();
    }
}
