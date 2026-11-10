using AiControlCenter.ControlPlane.Application;
using AiControlCenter.Grpc.Contracts.V1;
using Grpc.Core;

namespace AiControlCenter.ControlPlane.Api.Grpc;

//Цей клас отримує конфігурацію одного агента перед створенням запуску.
public sealed class AgentCatalogGrpcService(AgentDefinitionApplicationService agents)
    : AgentCatalog.AgentCatalogBase
{
    public override async Task<RunnableAgentReply> GetRunnableAgent(
        GetRunnableAgentRequest request,
        ServerCallContext context)
    {
        if (!Guid.TryParse(request.AgentId, out var id) || id == Guid.Empty)
            throw new RpcException(new Status(StatusCode.InvalidArgument, "A valid agent id is required."));

        try
        {
            var agent = await agents.GetRunnableSnapshotAsync(id, context.CancellationToken);
            return new RunnableAgentReply
            {
                AgentId = agent.AgentId.ToString(),
                AgentName = agent.AgentName,
                AgentCode = agent.AgentCode,
                AgentDescription = agent.AgentDescription ?? string.Empty,
                AgentVersion = agent.AgentVersion,
                ExecutionType = agent.ExecutionType.ToString(),
                DirectionId = agent.DirectionId.ToString(),
                DirectionName = agent.DirectionName,
                DirectionCode = agent.DirectionCode,
                DirectionVersion = agent.DirectionVersion,
                SystemPrompt = agent.SystemPrompt ?? string.Empty,
                ModelRoutesJson = agent.ModelRoutesJson ?? string.Empty,
                ToolPolicyJson = agent.ToolPolicyJson ?? string.Empty,
                MaxInputTokens = agent.MaxInputTokens ?? 0,
                MaxOutputTokens = agent.MaxOutputTokens ?? 0,
                MaxEstimatedCostUsd = agent.MaxEstimatedCostUsd?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty,
                MaxAttempts = agent.MaxAttempts,
            };
        }
        catch (AgentNotRunnableException)
        {
            throw new RpcException(new Status(
                StatusCode.FailedPrecondition,
                "Agent and direction must be active and available."));
        }
        catch (AgentDefinitionNotFoundException)
        {
            throw new RpcException(new Status(StatusCode.NotFound, "Agent was not found."));
        }
    }
}
