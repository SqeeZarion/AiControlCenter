using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;

namespace AiControlCenter.Integrations.Infrastructure.Providers;

internal sealed class DeterministicTestProvider : IAiProvider
{
    public AiProviderKind Kind => AiProviderKind.DeterministicTest;
    public Task<bool> ValidateAsync(string? credential, CancellationToken token) => Task.FromResult(true);
    public Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(string? credential, CancellationToken token) =>
        Task.FromResult<IReadOnlyCollection<ProviderModelDto>>([new("deterministic-v1", "Deterministic test model", 8192, true, false, 0, 0)]);
    public async Task<AiExecutionResult> ExecuteAsync(AiExecutionRequest request, string? credential, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var scenario = request.Scenario?.Trim().ToLowerInvariant() ?? "success";
        if (scenario == "timeout") return Failure(ProviderFailureKind.AmbiguousTimeout, "deterministic_timeout");
        if (scenario == "rate-limit") return Failure(ProviderFailureKind.RateLimited, "deterministic_rate_limit");
        if (scenario == "quota") return Failure(ProviderFailureKind.QuotaExceeded, "deterministic_quota");
        if (scenario == "quota-once" && request.IdempotencyKey.EndsWith(":1", StringComparison.Ordinal))
            return Failure(ProviderFailureKind.QuotaExceeded, "deterministic_quota_once");
        if (scenario == "unavailable") return Failure(ProviderFailureKind.TemporarilyUnavailable, "deterministic_unavailable");
        if (scenario == "failure") return Failure(ProviderFailureKind.Validation, "deterministic_failure");
        if (scenario is "tool" or "tool-quota-once" && (request.PriorToolResults?.Count ?? 0) == 0)
            return new(false, null, ProviderFailureKind.None, null, null, 0, 0, 0, [], request.ModelId,
                0, 0, ProviderMetricSource.ProviderReported, ProviderMetricSource.Estimated,
                "ToolCalls", null, [new("deterministic-tool-1", "system.utc-time", "{}")]);
        if (scenario == "tool-quota-once" && request.IdempotencyKey.EndsWith(":1", StringComparison.Ordinal))
            return Failure(ProviderFailureKind.QuotaExceeded, "deterministic_quota_after_tool");
        var output = $"Deterministic result: {request.Input}";
        return new(true, output, ProviderFailureKind.None, null, $"det-{request.IdempotencyKey}",
            Math.Max(1, request.Input.Length / 4), Math.Max(1, output.Length / 4), 0, [],
            request.ModelId, 0, 0, ProviderMetricSource.ProviderReported,
            ProviderMetricSource.Estimated, "Completed");
    }
    public async IAsyncEnumerable<AiExecutionChunk> ExecuteStreamingAsync(
        AiExecutionRequest request,
        string? credential,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var result = await ExecuteAsync(request, credential, token);
        if (result.Succeeded && result.Output is not null)
        {
            foreach (var part in result.Output.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                token.ThrowIfCancellationRequested();
                yield return new(part + " ", false, null);
            }
        }
        yield return new(null, true, result);
    }
    private static AiExecutionResult Failure(ProviderFailureKind kind, string code) => new(false, null, kind, code, null, 0, 0, 0, []);
}

internal sealed class SafeToolExecutor(TimeProvider timeProvider) : IToolExecutor
{
    private static readonly HashSet<string> Allowed = ["system.utc-time", "text.length"];
    private const int MaximumInputLength = 4096;
    private const int MaximumOutputLength = 8192;
    public async Task<ToolInvocationResult> ExecuteAsync(
        ToolCallRequest call,
        string idempotencyKey,
        CancellationToken token)
    {
        var startedAt = timeProvider.GetUtcNow();
        token.ThrowIfCancellationRequested();
        if (!Allowed.Contains(call.ToolName)) return Failure(call, idempotencyKey, "tool_not_allowed", startedAt);
        if (call.ArgumentsJson.Length > MaximumInputLength)
            return Failure(call, idempotencyKey, "tool_input_too_large", startedAt);
        string input;
        try
        {
            using var arguments = System.Text.Json.JsonDocument.Parse(call.ArgumentsJson, new System.Text.Json.JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = System.Text.Json.JsonCommentHandling.Disallow,
                MaxDepth = 4,
            });
            input = call.ToolName == "text.length"
                ? arguments.RootElement.GetProperty("text").GetString() ?? string.Empty
                : string.Empty;
        }
        catch (Exception exception) when (exception is System.Text.Json.JsonException
            or InvalidOperationException or KeyNotFoundException)
        {
            return Failure(call, idempotencyKey, "tool_arguments_invalid", startedAt);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        await Task.Yield();
        timeout.Token.ThrowIfCancellationRequested();
        var output = call.ToolName == "system.utc-time" ? timeProvider.GetUtcNow().ToString("O") : input.Length.ToString(System.Globalization.CultureInfo.InvariantCulture);
        output = new string(output.Where(character => !char.IsControl(character) || character is '\r' or '\n' or '\t').ToArray());
        if (output.Length > MaximumOutputLength)
            return Failure(call, idempotencyKey, "tool_output_too_large", startedAt);
        return new(call.ToolName, true, output, null, call.CallId, call.ArgumentsJson,
            "Succeeded", startedAt, timeProvider.GetUtcNow(), idempotencyKey);
    }
    private ToolInvocationResult Failure(ToolCallRequest call, string key, string error, DateTimeOffset startedAt) =>
        new(call.ToolName, false, null, error, call.CallId, call.ArgumentsJson,
            "Failed", startedAt, timeProvider.GetUtcNow(), key);
}
