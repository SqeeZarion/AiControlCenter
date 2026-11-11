using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AiControlCenter.Orchestrator.Domain;

namespace AiControlCenter.Orchestrator.UnitTests;

public sealed class AiRunLifecycleTests
{
    [Fact]
    public void AttemptPauseAndResumeKeepImmutableSnapshot()
    {
        var now = DateTimeOffset.UtcNow; var message = Guid.NewGuid();
        var routes = "[{\"connectionId\":\"00000000-0000-0000-0000-000000000001\",\"priority\":1}]";
        var run = AgentRun.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "corr",
            new AgentSnapshot("Agent", "agent", null, 1, "Ai", Guid.NewGuid(), "Direction", "direction", 1,
                "prompt", routes, "[]", 1000, 500, 1m, 3), "input", TestRunOutcome.Succeed, now);
        run.Begin(message, now.AddSeconds(1));
        var checkpoint = CreateCheckpoint(routes, 1, 1);
        run.RecordAttempt(message, 1, "OpenAi", "gpt-5", "Failed", "RateLimited", "req-1",
            10, 0, 0.1m, "gpt-5", "run:1", 1, 100, 0, "ProviderReported",
            "Estimated", "Failed", "RateLimited", "partial", checkpoint, now.AddSeconds(2));
        run.Pause(message, AgentRunStatus.PausedProviderUnavailable, "temporarily unavailable", now.AddSeconds(3));
        run.Resume(Guid.NewGuid());
        Assert.Equal(AgentRunStatus.Queued, run.Status);
        Assert.Single(run.Attempts);
        Assert.Equal(routes, run.ModelRoutesJson);
        Assert.Equal(checkpoint, run.CheckpointJson);
        Assert.Equal(1, run.CheckpointVersion);
        Assert.NotNull(run.CheckpointHash);
    }

    private static string CreateCheckpoint(string routes, int version, int cursor)
    {
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["checkpointVersion"] = version,
            ["logicalStepId"] = "ai-execution",
            ["confirmedContext"] = "input",
            ["partialOutput"] = "partial",
            ["partialOutputIsComplete"] = false,
            ["completedToolCalls"] = new JsonArray(),
            ["providerAttemptReferences"] = new JsonArray(),
            ["resumeCursor"] = cursor,
            ["lastSafeTimestamp"] = DateTimeOffset.UtcNow.ToString("O"),
            ["routeSnapshot"] = JsonNode.Parse(routes),
        };
        var payload = root.ToJsonString();
        root["hash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        return root.ToJsonString();
    }
}
