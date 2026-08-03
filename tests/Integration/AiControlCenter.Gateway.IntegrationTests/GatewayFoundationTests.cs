using AiControlCenter.Gateway;
using AiControlCenter.Gateway.Hubs;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Yarp.ReverseProxy.Configuration;

namespace AiControlCenter.Gateway.IntegrationTests;

public sealed class GatewayFoundationTests : IClassFixture<WebApplicationFactory<GatewayMarker>>
{
    private readonly WebApplicationFactory<GatewayMarker> factory;

    public GatewayFoundationTests(WebApplicationFactory<GatewayMarker> factory)
    {
        this.factory = factory.WithWebHostBuilder(_ => { });
    }

    [Fact]
    public async Task LivenessEndpointIsHealthy()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/live");
        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public void YarpContainsAllPublicServiceRoutes()
    {
        var provider = factory.Services.GetRequiredService<IProxyConfigProvider>();
        var routeIds = provider.GetConfig().Routes.Select(route => route.RouteId).ToHashSet();

        Assert.Contains("identity", routeIds);
        Assert.Contains("control-plane", routeIds);
        Assert.Contains("orchestrator", routeIds);
        Assert.Contains("integrations", routeIds);
    }

    [Fact]
    public async Task SignalRTechnicalHandshakeAndPingSucceed()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/system", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();

        await connection.StartAsync();
        var pong = await connection.InvokeAsync<TechnicalPong>("Ping");

        Assert.Equal("gateway", pong.Service);
        Assert.Equal(HubConnectionState.Connected, connection.State);
    }
}
