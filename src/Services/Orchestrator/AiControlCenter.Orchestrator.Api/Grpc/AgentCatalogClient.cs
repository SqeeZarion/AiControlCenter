using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Orchestrator.Application;
using AiControlCenter.Orchestrator.Domain;
using Grpc.Core;

namespace AiControlCenter.Orchestrator.Api.Grpc;

public sealed class AgentCatalogClient(
    AiControlCenter.Grpc.Contracts.V1.AgentCatalog.AgentCatalogClient client,
    TimeProvider timeProvider) : IAgentCatalogClient
{
    public async Task<AgentSnapshot> GetRunnableAgentAsync(
        Guid agentId,
        string delegatedAuthorization,
        CancellationToken cancellationToken)
    {
        try
        {
            var reply = await client.GetRunnableAgentAsync(
                new GetRunnableAgentRequest { AgentId = agentId.ToString() },
                new Metadata { { "Authorization", delegatedAuthorization } },
                deadline: timeProvider.GetUtcNow().UtcDateTime.AddSeconds(5),
                cancellationToken: cancellationToken);
            return new AgentSnapshot(
                reply.AgentName, reply.AgentCode,
                string.IsNullOrEmpty(reply.AgentDescription) ? null : reply.AgentDescription,
                reply.AgentVersion, reply.ExecutionType,
                Guid.Parse(reply.DirectionId), reply.DirectionName, reply.DirectionCode, reply.DirectionVersion);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.FailedPrecondition)
        {
            throw new AgentRunConflictException("Agent and direction must be active and available.");
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.NotFound)
        {
            throw new AgentRunAgentNotFoundException(agentId);
        }
        catch (RpcException exception) when (exception.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            throw new AgentCatalogUnavailableException(exception);
        }
    }
}
