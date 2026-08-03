using AiControlCenter.ControlPlane.Api;
using AiControlCenter.Grpc.Contracts.V1;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class GrpcServiceInfoTests
{
    [Fact]
    public async Task ControlPlaneExposesVersionedServiceInfo()
    {
        await using var factory = new WebApplicationFactory<ControlPlaneApiMarker>()
            .WithWebHostBuilder(builder => builder.UseSetting(
                "ConnectionStrings:ControlPlaneDatabase",
                "Host=127.0.0.1;Port=1;Database=foundation;Username=test;Password=test;Timeout=1"));

        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
        {
            HttpHandler = factory.Server.CreateHandler(),
        });
        var client = new ServiceInfo.ServiceInfoClient(channel);

        var reply = await client.GetServiceInfoAsync(new ServiceInfoRequest { Caller = "integration-tests" });

        Assert.Equal("AiControlCenter.ControlPlane.Api", reply.ServiceName);
        Assert.Equal("v1", reply.Version);
    }
}
