using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;

namespace AiControlCenter.Integrations.Infrastructure.Providers;

internal abstract class OpenAiCompatibleProvider(HttpClient httpClient) : IAiProvider
{
    protected abstract string ModelsPath { get; }
    protected virtual string ResponsesPath => "v1/responses";
    public abstract AiProviderKind Kind { get; }

    public async Task<bool> ValidateAsync(string? credential, CancellationToken token)
    {
        using var request = CreateRequest(HttpMethod.Get, ModelsPath, credential);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        return response.IsSuccessStatusCode;
    }

    public async Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(string? credential, CancellationToken token)
    {
        using var request = CreateRequest(HttpMethod.Get, ModelsPath, credential);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: token);
        return payload?["data"]?.AsArray().Select(node =>
        {
            var id = node?["id"]?.GetValue<string>() ?? "unknown";
            var rates = ProviderPricingCatalog.Rates(Kind, id);
            return new ProviderModelDto(id, id, null, true, false, rates?.Input, rates?.Output);
        }).ToArray() ?? [];
    }

    public async Task<AiExecutionResult> ExecuteAsync(AiExecutionRequest input, string? credential, CancellationToken token)
    {
        var started = Stopwatch.GetTimestamp();
        using var request = CreateRequest(HttpMethod.Post, ResponsesPath, credential);
        request.Headers.TryAddWithoutValidation("Idempotency-Key", input.IdempotencyKey);
        request.Content = JsonContent.Create(CreatePayload(input));
        using var response = await httpClient.SendAsync(request, token);
        var requestId = response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null;
        if (!response.IsSuccessStatusCode) return Failure(response.StatusCode, requestId);
        var payload = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken: token);
        var output = payload?["output_text"]?.GetValue<string>()
            ?? ExtractOutput(payload?["output"] as JsonArray);
        var toolCalls = ExtractToolCalls(payload?["output"] as JsonArray);
        var inputTokens = payload?["usage"]?["input_tokens"]?.GetValue<int>() ?? 0;
        var outputTokens = payload?["usage"]?["output_tokens"]?.GetValue<int>() ?? 0;
        var cachedInputTokens = payload?["usage"]?["input_tokens_details"]?["cached_tokens"]?.GetValue<int>();
        var actualModel = payload?["model"]?.GetValue<string>() ?? input.ModelId;
        if (toolCalls.Length > 0)
            return new(false, null, ProviderFailureKind.None, null, requestId, inputTokens, outputTokens,
                EstimateCost(actualModel, inputTokens, outputTokens), [], actualModel,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, cachedInputTokens,
                ProviderMetricSource.ProviderReported, ProviderMetricSource.Estimated,
                "ToolCalls", output, toolCalls);
        return new(true, output, ProviderFailureKind.None, null, requestId, inputTokens, outputTokens,
            EstimateCost(actualModel, inputTokens, outputTokens), [], actualModel,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds, cachedInputTokens,
            ProviderMetricSource.ProviderReported, ProviderMetricSource.Estimated, "Completed");
    }

    public IAsyncEnumerable<AiExecutionChunk> ExecuteStreamingAsync(
        AiExecutionRequest request,
        string? credential,
        CancellationToken token) => AiProviderStreaming.BufferAsync(this, request, credential, token);

    protected virtual decimal? EstimateCost(string model, int inputTokens, int outputTokens) =>
        ProviderPricingCatalog.Estimate(Kind, model, inputTokens, outputTokens);
    private static string? ExtractOutput(JsonArray? output) => output?.SelectMany(item =>
        item?["content"]?.AsArray() ?? []).Select(x => x?["text"]?.GetValue<string>())
        .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    private static ToolCallRequest[] ExtractToolCalls(JsonArray? output) => output?
        .Where(item => string.Equals(item?["type"]?.GetValue<string>(), "function_call", StringComparison.Ordinal))
        .Select(item => new ToolCallRequest(
            item?["call_id"]?.GetValue<string>() ?? string.Empty,
            item?["name"]?.GetValue<string>() ?? string.Empty,
            item?["arguments"]?.GetValue<string>() ?? "{}"))
        .Where(call => !string.IsNullOrWhiteSpace(call.CallId) && !string.IsNullOrWhiteSpace(call.ToolName))
        .ToArray() ?? [];
    private static JsonObject CreatePayload(AiExecutionRequest input)
    {
        JsonNode requestInput = JsonValue.Create(input.Input)!;
        if (input.PriorToolResults is { Count: > 0 })
        {
            var turns = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = input.Input,
                },
            };
            foreach (var result in input.PriorToolResults)
            {
                turns.Add(new JsonObject
                {
                    ["type"] = "function_call",
                    ["call_id"] = result.CallId,
                    ["name"] = result.ToolName,
                    ["arguments"] = result.ArgumentsJson,
                });
                turns.Add(new JsonObject
                {
                    ["type"] = "function_call_output",
                    ["call_id"] = result.CallId,
                    ["output"] = result.Succeeded ? result.Output : result.Error,
                });
            }
            requestInput = turns;
        }
        var tools = new JsonArray();
        foreach (var definition in input.ToolDefinitions ?? [])
        {
            tools.Add(new JsonObject
            {
                ["type"] = "function",
                ["name"] = definition.Name,
                ["description"] = definition.Description,
                ["parameters"] = JsonNode.Parse(definition.InputJsonSchema),
                ["strict"] = true,
            });
        }
        var payload = new JsonObject
        {
            ["model"] = input.ModelId,
            ["instructions"] = input.SystemPrompt,
            ["input"] = requestInput,
            ["max_output_tokens"] = input.MaxOutputTokens,
        };
        if (tools.Count > 0) payload["tools"] = tools;
        return payload;
    }
    private static AiExecutionResult Failure(HttpStatusCode status, string? requestId)
    {
        var kind = status switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => ProviderFailureKind.Authentication,
            HttpStatusCode.TooManyRequests => ProviderFailureKind.RateLimited,
            HttpStatusCode.NotFound => ProviderFailureKind.ModelUnavailable,
            HttpStatusCode.PaymentRequired => ProviderFailureKind.InsufficientFunds,
            >= HttpStatusCode.InternalServerError => ProviderFailureKind.TemporarilyUnavailable,
            _ => ProviderFailureKind.Validation,
        };
        return new(false, null, kind, $"provider_http_{(int)status}", requestId, 0, 0, null, []);
    }
    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, string? credential)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential ?? string.Empty);
        return request;
    }
}

internal sealed class OpenAiProvider(HttpClient client) : OpenAiCompatibleProvider(client)
{
    protected override string ModelsPath => "v1/models";
    public override AiProviderKind Kind => AiProviderKind.OpenAi;
}

internal sealed class XAiProvider(HttpClient client) : OpenAiCompatibleProvider(client)
{
    protected override string ModelsPath => "v1/models";
    public override AiProviderKind Kind => AiProviderKind.XAi;
}
