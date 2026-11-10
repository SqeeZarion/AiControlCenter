using AiControlCenter.ControlPlane.Application;
using AiControlCenter.Grpc.Contracts.V1;
using Grpc.Core;

namespace AiControlCenter.ControlPlane.Api.Grpc;

public sealed class AgentModelRouteValidator(
    AiProviderExecution.AiProviderExecutionClient client,
    IConfiguration configuration) : IAgentModelRouteValidator
{
    public async Task ValidateAsync(
        Guid ownerUserId,
        string logicalRoutesJson,
        CancellationToken cancellationToken)
    {
        var apiKey = configuration["WorkerGrpc:ApiKey"];
        if (string.IsNullOrWhiteSpace(apiKey))
            throw new InvalidOperationException("WorkerGrpc:ApiKey is required.");
        try
        {
            var reply = await client.ResolveRoutesAsync(new ResolveAiProviderRoutesRequest
            {
                OwnerUserId = ownerUserId.ToString(),
                LogicalRoutesJson = logicalRoutesJson,
            }, new Metadata { { "x-worker-api-key", apiKey } },
                cancellationToken: cancellationToken);
            if (!reply.AllAvailable)
                throw new AgentModelRouteValidationException(
                    "AgentRouteConnectionUnavailable",
                    "modelRoutesJson",
                    "Вибране підключення недоступне, неактивне або належить іншому користувачу.");
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.Cancelled
            && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "AI provider route validation was cancelled.", exception, cancellationToken);
        }
        catch (RpcException exception) when (exception.StatusCode == StatusCode.InvalidArgument)
        {
            throw new AgentModelRouteValidationException(
                "AgentRouteInvalid",
                "modelRoutesJson",
                "Конфігурація AI-маршруту некоректна або вибрана модель недоступна.");
        }
        catch (RpcException exception) when (exception.StatusCode is StatusCode.Unavailable
            or StatusCode.DeadlineExceeded or StatusCode.Cancelled)
        {
            throw new AgentModelRouteValidationException(
                "AgentRouteValidationUnavailable",
                "modelRoutesJson",
                "Перевірка підключення AI-провайдера тимчасово недоступна. Повторіть спробу пізніше.",
                StatusCodes.Status503ServiceUnavailable);
        }
    }
}
