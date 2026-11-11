using AiControlCenter.ControlPlane.Application;
using AiControlCenter.ControlPlane.Domain;

namespace AiControlCenter.ControlPlane.UnitTests;

public sealed class AiAgentDefinitionTests
{
    [Fact]
    public async Task ValidatorAcceptsOrderedRoutesAndRejectsDuplicatePriorities()
    {
        var validator = new CreateAgentDefinitionCommandValidator();
        var valid = Command("[{\"connectionName\":\"Personal OpenAI\",\"provider\":\"OpenAi\",\"modelId\":\"gpt-5\",\"priority\":1,\"enabled\":true,\"paidExecutionAllowed\":true,\"safetyCapUsd\":0.5,\"dailyCostCapUsd\":5,\"monthlyCostCapUsd\":50,\"maxRequestsPerDay\":20,\"maxRequestsPerMonth\":200}]");
        Assert.True((await validator.ValidateAsync(valid, CancellationToken.None)).IsValid);
        var duplicate = valid with { ModelRoutesJson = "[{\"provider\":\"DeterministicTest\",\"modelId\":\"a\",\"priority\":1},{\"provider\":\"DeterministicTest\",\"modelId\":\"b\",\"priority\":1}]" };
        Assert.False((await validator.ValidateAsync(duplicate, CancellationToken.None)).IsValid);
    }

    [Fact]
    public async Task ValidatorAcceptsExplicitLocalCopilotCliRoute()
    {
        var connectionId = Guid.NewGuid();
        var command = Command($$"""[{"provider":"LocalCopilotCli","connectionId":"{{connectionId}}","connectionName":"Local Copilot CLI","modelId":"Auto","priority":1,"paidExecutionAllowed":false,"paidFallbackAllowed":false}]""");

        Assert.True((await new CreateAgentDefinitionCommandValidator()
            .ValidateAsync(command, CancellationToken.None)).IsValid);
    }

    [Fact]
    public async Task ValidatorRequiresExplicitPaidPermissionAndBoundedSafetyCap()
    {
        var validator = new CreateAgentDefinitionCommandValidator();
        var implicitPaid = Command("[{\"provider\":\"OpenAi\",\"modelId\":\"gpt-5\",\"priority\":1}]");
        var excessiveCap = Command("[{\"provider\":\"XAi\",\"modelId\":\"grok-4.3\",\"priority\":1,\"paidExecutionAllowed\":true,\"safetyCapUsd\":2,\"dailyCostCapUsd\":5,\"monthlyCostCapUsd\":50,\"maxRequestsPerDay\":20,\"maxRequestsPerMonth\":200}]");

        Assert.False((await validator.ValidateAsync(implicitPaid, CancellationToken.None)).IsValid);
        Assert.False((await validator.ValidateAsync(excessiveCap, CancellationToken.None)).IsValid);
    }

    [Fact]
    public async Task ValidatorRejectsUnsupportedFallbackReasonAndUnboundedAttempts()
    {
        var validator = new CreateAgentDefinitionCommandValidator();
        var command = Command("[{\"provider\":\"DeterministicTest\",\"modelId\":\"deterministic-v1\",\"priority\":1,\"maxAttempts\":4,\"allowedFallbackReasons\":[\"Authentication\"]}]");

        Assert.False((await validator.ValidateAsync(command, CancellationToken.None)).IsValid);
    }

    [Theory]
    [InlineData("[{\"provider\":\"DeterministicTest\",\"modelId\":\"deterministic-v1\",\"priority\":1,\"unknown\":true}]")]
    [InlineData("[{\"provider\":\"DeterministicTest\",\"modelId\":\"deterministic-v1\",\"priority\":1,\"connectionId\":\"not-a-guid\"}]")]
    [InlineData("[{\"provider\":99,\"modelId\":\"deterministic-v1\",\"priority\":1}]")]
    public async Task ValidatorRejectsUnknownFieldsAndInvalidProviderShape(string routes)
    {
        var result = await new CreateAgentDefinitionCommandValidator()
            .ValidateAsync(Command(routes), CancellationToken.None);

        Assert.False(result.IsValid);
    }

    [Fact]
    public void AggregateClearsAiSecretsWhenChangedToTest()
    {
        const string route = "[{\"provider\":\"OpenAi\",\"modelId\":\"gpt-5\",\"priority\":1}]";
        var agent = AgentDefinition.Create(Guid.NewGuid(), Guid.NewGuid(), "Agent", "agent", null,
            AgentDefinitionStatus.Active, AgentExecutionType.Ai, DateTimeOffset.UtcNow, "prompt", route, "[]", 100, 100, 1, 2);
        agent.Update(agent.DirectionId, "Agent", "agent", null, AgentDefinitionStatus.Active,
            AgentExecutionType.Test, DateTimeOffset.UtcNow.AddSeconds(1));
        Assert.Null(agent.SystemPrompt); Assert.Null(agent.ModelRoutesJson); Assert.Equal(1, agent.MaxAttempts);
    }

    private static CreateAgentDefinitionCommand Command(string routes) => new(Guid.NewGuid(), "AI agent", "ai-agent", null,
        AgentDefinitionStatus.Active, AgentExecutionType.Ai, "prompt", routes, "[\"system.utc-time\"]", 1000, 500, 1m, 3);
}
