using AiControlCenter.Contracts;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Observability;

namespace AiControlCenter.BuildingBlocks.UnitTests;

public sealed class CorrelationIdTests
{
    [Theory]
    [InlineData(" request-42 ", "request-42")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    public void NormalizeReturnsExpectedValue(string value, string? expected)
    {
        Assert.Equal(expected, CorrelationIdMiddleware.Normalize(value));
    }

    [Fact]
    public void NormalizeRejectsControlCharactersAndOversizedValues()
    {
        Assert.Null(CorrelationIdMiddleware.Normalize("request\n42"));
        Assert.Null(CorrelationIdMiddleware.Normalize(new string('a', 129)));
    }

    [Fact]
    public void TechnicalContractsAreVersioned()
    {
        var reply = new ServiceInfoReply { Version = ContractVersion.V1 };
        Assert.Equal("v1", reply.Version);
    }
}
