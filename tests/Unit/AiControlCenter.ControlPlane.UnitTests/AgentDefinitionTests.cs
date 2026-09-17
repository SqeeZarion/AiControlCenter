using AiControlCenter.ControlPlane.Domain;

namespace AiControlCenter.ControlPlane.UnitTests;

public sealed class AgentDefinitionTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void CreateNormalizesUnicodeNameAndCode()
    {
        var agent = Create("\u0085Test\u00A0 Agent\u202F", " Test__Agent ");

        Assert.Equal("Test Agent", agent.Name);
        Assert.Equal("test-agent", agent.Code);
        Assert.Equal(AgentExecutionType.Test, agent.ExecutionType);
        Assert.Equal(Now, agent.CreatedAt);
    }

    [Fact]
    public void UpdateIsAtomicWhenOneValueIsInvalid()
    {
        var agent = Create("Original", "original");
        var originalDirectionId = agent.DirectionId;

        Assert.Throws<ArgumentException>(() => agent.Update(
            Guid.NewGuid(), "Changed", "invalid/code", "changed",
            AgentDefinitionStatus.Inactive, AgentExecutionType.Test, Now.AddMinutes(1)));

        Assert.Equal(originalDirectionId, agent.DirectionId);
        Assert.Equal("Original", agent.Name);
        Assert.Equal("original", agent.Code);
        Assert.Equal(AgentDefinitionStatus.Active, agent.Status);
        Assert.Equal(Now, agent.UpdatedAt);
    }

    [Fact]
    public void ArchiveAndRestoreAreIdempotentAndBlockMutations()
    {
        var agent = Create("Agent", "agent");

        Assert.True(agent.Archive(Now.AddMinutes(1)));
        Assert.False(agent.Archive(Now.AddMinutes(2)));
        Assert.Throws<AgentDefinitionRuleViolationException>(() =>
            agent.ChangeStatus(AgentDefinitionStatus.Inactive, Now.AddMinutes(3)));
        Assert.True(agent.Restore(Now.AddMinutes(4)));
        Assert.False(agent.Restore(Now.AddMinutes(5)));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(101)]
    public void NameUsesUnicodeScalarBoundaries(int scalarCount)
    {
        var name = string.Concat(Enumerable.Repeat("😀", scalarCount));

        Assert.Throws<ArgumentException>(() => Create(name, "unicode-boundary"));
    }

    private static AgentDefinition Create(string name, string code) => AgentDefinition.Create(
        Guid.NewGuid(), Guid.NewGuid(), name, code, null,
        AgentDefinitionStatus.Active, AgentExecutionType.Test, Now);
}
