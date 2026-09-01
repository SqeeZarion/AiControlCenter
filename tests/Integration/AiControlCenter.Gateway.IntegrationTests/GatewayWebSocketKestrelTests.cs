using System.Net;
using AiControlCenter.Gateway;
using AiControlCenter.Gateway.Hubs;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;

namespace AiControlCenter.Gateway.IntegrationTests;

public sealed class GatewayWebSocketKestrelTests
{
    [Fact]
    public async Task RealWebSocketAcceptsQueryAccessTokenAndInvokesPing()
    {
        await using var factory = CreateKestrelFactory();
        using var client = factory.CreateClient();
        await using var connection = CreateWebSocketConnection(
            client.BaseAddress!,
            TestJwtTokenFactory.Issue());

        await connection.StartAsync();
        var pong = await connection.InvokeAsync<TechnicalPong>("Ping");

        Assert.Equal(HubConnectionState.Connected, connection.State);
        Assert.Equal("gateway", pong.Service);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-jwt")]
    public async Task RealWebSocketRejectsMissingOrInvalidQueryAccessToken(string? accessToken)
    {
        await using var factory = CreateKestrelFactory();
        using var client = factory.CreateClient();
        await using var connection = CreateWebSocketConnection(client.BaseAddress!, accessToken);

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());

        Assert.Equal(HubConnectionState.Disconnected, connection.State);
    }

    [Fact]
    public async Task QueryAccessTokenIsIgnoredOutsideTheSignalRHubPath()
    {
        await using var factory = CreateKestrelFactory();
        using var client = factory.CreateClient();
        var token = Uri.EscapeDataString(TestJwtTokenFactory.Issue());

        using var response = await client.GetAsync($"/api/gateway/service-info?access_token={token}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private static WebApplicationFactory<GatewayMarker> CreateKestrelFactory()
    {
        var factory = new WebApplicationFactory<GatewayMarker>()
            .WithWebHostBuilder(builder => builder
                .UseEnvironment("Production")
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath));
        factory.UseKestrel(0);
        return factory;
    }

    private static HubConnection CreateWebSocketConnection(Uri baseAddress, string? accessToken)
    {
        var path = "/hubs/system";
        if (accessToken is not null)
        {
            path += $"?access_token={Uri.EscapeDataString(accessToken)}";
        }

        return new HubConnectionBuilder()
            .WithUrl(new Uri(baseAddress, path), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
            })
            .Build();
    }
}
