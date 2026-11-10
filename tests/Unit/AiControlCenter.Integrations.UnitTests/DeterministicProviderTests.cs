using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Infrastructure.Providers;

namespace AiControlCenter.Integrations.UnitTests;

public sealed class DeterministicProviderTests
{
    public static TheoryData<string, ProviderFailureKind, bool> Failures => new()
    {
        { "failure", ProviderFailureKind.Validation, false },
        { "rate-limit", ProviderFailureKind.RateLimited, true },
        { "quota", ProviderFailureKind.QuotaExceeded, true },
        { "unavailable", ProviderFailureKind.TemporarilyUnavailable, true },
        { "timeout", ProviderFailureKind.AmbiguousTimeout, false },
    };

    [Fact]
    public async Task SuccessIsDeterministicAndReportsUsage()
    {
        var provider = Create();
        var request = Request("success");
        var first = await provider.ExecuteAsync(request, null, CancellationToken.None);
        var second = await provider.ExecuteAsync(request, null, CancellationToken.None);
        Assert.True(first.Succeeded);
        Assert.Equal(first.Output, second.Output);
        Assert.Equal(first.ProviderRequestId, second.ProviderRequestId);
        Assert.Equal(first.InputTokens, second.InputTokens);
        Assert.Equal(first.OutputTokens, second.OutputTokens);
        Assert.Empty(first.ToolInvocations);
        Assert.Empty(second.ToolInvocations);
        Assert.True(first.InputTokens > 0);
        Assert.Equal(0m, first.EstimatedCostUsd);
    }

    [Theory, MemberData(nameof(Failures))]
    public async Task FailureScenariosHaveStrictFallbackClassification(string scenario, ProviderFailureKind expected, bool canFallback)
    {
        var result = await Create().ExecuteAsync(Request(scenario), null, CancellationToken.None);
        Assert.False(result.Succeeded); Assert.Equal(expected, result.FailureKind);
        Assert.Equal(canFallback, ProviderFallbackPolicy.CanFallback(result.FailureKind));
    }

    [Fact]
    public async Task ToolScenarioUsesOnlyAllowlistedServerTool()
    {
        var result = await Create().ExecuteAsync(Request("tool") with { AllowedTools = ["system.utc-time"] }, null,
            CancellationToken.None);
        Assert.False(result.Succeeded);
        var call = Assert.Single(result.RequestedToolCalls!);
        Assert.Equal("system.utc-time", call.ToolName);
    }

    [Fact]
    public async Task StreamingContractProducesDeterministicDeltasAndOneFinalResult()
    {
        var chunks = new List<AiExecutionChunk>();
        await foreach (var chunk in Create().ExecuteStreamingAsync(
            Request("success"), null, CancellationToken.None))
            chunks.Add(chunk);

        Assert.NotEmpty(chunks);
        Assert.Single(chunks, chunk => chunk.IsFinal);
        Assert.True(chunks[^1].Result?.Succeeded);
        Assert.Equal(
            chunks[^1].Result!.Output + " ",
            string.Concat(chunks.Where(chunk => !chunk.IsFinal).Select(chunk => chunk.Delta)));
    }

    [Fact]
    public async Task QuotaOncePausesFirstAttemptAndSucceedsAfterResume()
    {
        var provider = Create();
        var first = await provider.ExecuteAsync(
            Request("quota-once") with { IdempotencyKey = "run:1" }, null, CancellationToken.None);
        var resumed = await provider.ExecuteAsync(
            Request("quota-once") with { IdempotencyKey = "run:2" }, null, CancellationToken.None);

        Assert.False(first.Succeeded);
        Assert.Equal(ProviderFailureKind.QuotaExceeded, first.FailureKind);
        Assert.True(resumed.Succeeded);
    }

    private static DeterministicTestProvider Create() => new();
    private static AiExecutionRequest Request(string scenario) => new(Guid.NewGuid(), Guid.NewGuid(), "deterministic-v1",
        "system", "input", [], scenario, "idem-1");
}
