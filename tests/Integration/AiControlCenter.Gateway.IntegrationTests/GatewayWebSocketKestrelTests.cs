using System.Collections.Concurrent;
using System.Net;
using AiControlCenter.Contracts.V1;
using AiControlCenter.Gateway;
using AiControlCenter.Gateway.Hubs;
using AiControlCenter.Gateway.Messaging;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

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

    [Fact]
    public async Task RunEventsReachEveryAuthorizedAudienceExactlyOnceThroughProductionConsumer()
    {
        await using var factory = CreateKestrelFactory();
        using var client = factory.CreateClient();
        var userId = Guid.NewGuid();
        var developerId = Guid.NewGuid();
        var adminOwnerId = Guid.NewGuid();
        var adminObserverId = Guid.NewGuid();
        await using var user = CreateWebSocketConnection(client.BaseAddress!, TestJwtTokenFactory.Issue("User", userId));
        await using var developer = CreateWebSocketConnection(client.BaseAddress!, TestJwtTokenFactory.Issue("Developer", developerId));
        await using var adminOwner = CreateWebSocketConnection(client.BaseAddress!, TestJwtTokenFactory.Issue("Admin", adminOwnerId));
        await using var adminObserver = CreateWebSocketConnection(client.BaseAddress!, TestJwtTokenFactory.Issue("Admin", adminObserverId));
        await using var stranger = CreateWebSocketConnection(client.BaseAddress!, TestJwtTokenFactory.Issue("User", Guid.NewGuid()));
        var userEvents = new RunEventProbe(user);
        var developerEvents = new RunEventProbe(developer);
        var adminOwnerEvents = new RunEventProbe(adminOwner);
        var adminObserverEvents = new RunEventProbe(adminObserver);
        var strangerEvents = new RunEventProbe(stranger);
        await Task.WhenAll(
            user.StartAsync(), developer.StartAsync(), adminOwner.StartAsync(),
            adminObserver.StartAsync(), stranger.StartAsync());
        var publish = factory.Services.GetRequiredService<IPublishEndpoint>();

        var userEvent = CreateRunEvent(Guid.NewGuid(), userId, 1);
        await publish.Publish(userEvent);
        await Task.WhenAll(
            userEvents.WaitForEventCountAsync(userEvent.EventId, 1),
            adminOwnerEvents.WaitForEventCountAsync(userEvent.EventId, 1),
            adminObserverEvents.WaitForEventCountAsync(userEvent.EventId, 1));

        var developerEvent = CreateRunEvent(Guid.NewGuid(), developerId, 1);
        await publish.Publish(developerEvent);
        await Task.WhenAll(
            developerEvents.WaitForEventCountAsync(developerEvent.EventId, 1),
            adminOwnerEvents.WaitForEventCountAsync(developerEvent.EventId, 1),
            adminObserverEvents.WaitForEventCountAsync(developerEvent.EventId, 1));

        var adminOwnedRunId = Guid.NewGuid();
        var adminEvents = Enumerable.Range(1, 3)
            .Select(revision => CreateRunEvent(adminOwnedRunId, adminOwnerId, revision))
            .ToArray();
        foreach (var message in adminEvents)
            await publish.Publish(message);
        await Task.WhenAll(
            adminOwnerEvents.WaitForRunCountAsync(adminOwnedRunId, adminEvents.Length),
            adminObserverEvents.WaitForRunCountAsync(adminOwnedRunId, adminEvents.Length));
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.Equal(1, userEvents.Count(userEvent.EventId));
        Assert.Equal(1, developerEvents.Count(developerEvent.EventId));
        Assert.Equal(1, adminOwnerEvents.Count(userEvent.EventId));
        Assert.Equal(1, adminObserverEvents.Count(userEvent.EventId));
        Assert.Equal(1, adminOwnerEvents.Count(developerEvent.EventId));
        Assert.Equal(1, adminObserverEvents.Count(developerEvent.EventId));
        Assert.Equal([1L, 2L, 3L], adminOwnerEvents.Revisions(adminOwnedRunId));
        Assert.Equal([1L, 2L, 3L], adminObserverEvents.Revisions(adminOwnedRunId));
        Assert.Empty(userEvents.ForRun(adminOwnedRunId));
        Assert.Empty(developerEvents.ForRun(adminOwnedRunId));
        Assert.Empty(strangerEvents.All);
    }

    [Fact]
    public async Task ReconnectRemovesTheOldConnectionFromRunDelivery()
    {
        await using var factory = CreateKestrelFactory();
        using var client = factory.CreateClient();
        var ownerId = Guid.NewGuid();
        await using var oldConnection = CreateWebSocketConnection(
            client.BaseAddress!, TestJwtTokenFactory.Issue("User", ownerId));
        var oldEvents = new RunEventProbe(oldConnection);
        await oldConnection.StartAsync();
        var publish = factory.Services.GetRequiredService<IPublishEndpoint>();
        var runId = Guid.NewGuid();
        var beforeReconnect = CreateRunEvent(runId, ownerId, 1);
        await publish.Publish(beforeReconnect);
        await oldEvents.WaitForEventCountAsync(beforeReconnect.EventId, 1);
        await oldConnection.StopAsync();

        await using var newConnection = CreateWebSocketConnection(
            client.BaseAddress!, TestJwtTokenFactory.Issue("User", ownerId));
        var newEvents = new RunEventProbe(newConnection);
        await newConnection.StartAsync();
        var afterReconnect = CreateRunEvent(runId, ownerId, 2);
        await publish.Publish(afterReconnect);
        await newEvents.WaitForEventCountAsync(afterReconnect.EventId, 1);
        await Task.Delay(TimeSpan.FromMilliseconds(500));

        Assert.Equal(1, oldEvents.Count(beforeReconnect.EventId));
        Assert.Equal(0, oldEvents.Count(afterReconnect.EventId));
        Assert.Equal(1, newEvents.Count(afterReconnect.EventId));
    }

    private static WebApplicationFactory<GatewayMarker> CreateKestrelFactory()
    {
        var factory = new WebApplicationFactory<GatewayMarker>()
            .WithWebHostBuilder(builder => builder
                .UseEnvironment("Production")
                .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
                .ConfigureServices(services => services.AddMassTransitTestHarness(configurator =>
                    configurator.AddConsumer<RunStatusChangedConsumer>())));
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

    private static RunStatusChangedV1 CreateRunEvent(Guid runId, Guid ownerId, long revision) => new(
        Guid.NewGuid(), runId, ownerId, "Running", revision,
        DateTimeOffset.UtcNow, checked((int)revision), "Running");

    private sealed class RunEventProbe
    {
        private readonly ConcurrentQueue<RunStatusChangedV1> events = new();
        public RunEventProbe(HubConnection connection) =>
            connection.On<RunStatusChangedV1>("RunStatusChanged", message =>
            {
                events.Enqueue(message);
            });

        public RunStatusChangedV1[] All => events.ToArray();

        public int Count(Guid eventId) => events.Count(message => message.EventId == eventId);

        public RunStatusChangedV1[] ForRun(Guid runId) =>
            events.Where(message => message.RunId == runId).ToArray();

        public long[] Revisions(Guid runId) =>
            events.Where(message => message.RunId == runId).Select(message => message.Revision).ToArray();

        public Task WaitForEventCountAsync(Guid eventId, int count) =>
            WaitUntilAsync(() => Count(eventId) >= count);

        public Task WaitForRunCountAsync(Guid runId, int count) =>
            WaitUntilAsync(() => events.Count(message => message.RunId == runId) >= count);

        private static async Task WaitUntilAsync(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token);
        }
    }
}
