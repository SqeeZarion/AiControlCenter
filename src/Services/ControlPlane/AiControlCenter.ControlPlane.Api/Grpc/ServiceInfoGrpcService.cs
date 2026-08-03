using AiControlCenter.Grpc.Contracts.V1;
using Grpc.Core;

namespace AiControlCenter.ControlPlane.Api.Grpc;

public sealed class ServiceInfoGrpcService(IHostEnvironment environment) : ServiceInfo.ServiceInfoBase
{
    public override Task<ServiceInfoReply> GetServiceInfo(
        ServiceInfoRequest request,
        ServerCallContext context)
    {
        return Task.FromResult(new ServiceInfoReply
        {
            ServiceName = environment.ApplicationName,
            Version = "v1",
            Environment = environment.EnvironmentName,
        });
    }
}
