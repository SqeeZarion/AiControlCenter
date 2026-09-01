using System.Text.Json;
using AiControlCenter.Gateway;
using AiControlCenter.Gateway.Hubs;
using AiControlCenter.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Yarp.ReverseProxy.Configuration;

namespace AiControlCenter.Gateway.IntegrationTests;

public sealed class GatewayFoundationTests : IClassFixture<WebApplicationFactory<GatewayMarker>>
{
    private readonly WebApplicationFactory<GatewayMarker> factory;

    public GatewayFoundationTests(WebApplicationFactory<GatewayMarker> factory)
    {
        this.factory = factory.WithWebHostBuilder(builder => builder.UseSetting(
            "Authentication:PublicKeyPath",
            TestJwtTokenFactory.PublicKeyPath));
    }

    [Fact]
    public async Task LivenessEndpointIsHealthy()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/live");
        response.EnsureSuccessStatusCode();
    }

    [Theory]
    [MemberData(nameof(PrivatePublicKeyPaths))]
    public async Task PrivatePemInPublicKeyPathFailsGatewayStartup(string publicKeyPath)
    {
        await using var invalidFactory = factory.WithWebHostBuilder(builder =>
            builder.UseSetting("Authentication:PublicKeyPath", publicKeyPath));

        await AssertStartupFailsAsync(invalidFactory, "Authentication:PublicKeyPath contains private key material");
    }

    [Fact]
    public async Task MalformedPublicKeyFailsGatewayStartup()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aicontrolcenter-gateway-malformed-{Guid.NewGuid():N}.pem");
        await File.WriteAllTextAsync(path, "not-an-rsa-key");
        try
        {
            await using var invalidFactory = factory.WithWebHostBuilder(builder =>
                builder.UseSetting("Authentication:PublicKeyPath", path));
            await AssertStartupFailsAsync(invalidFactory, "valid RSA public PEM key");
        }
        finally
        {
            File.Delete(path);
        }
    }

    public static TheoryData<string> PrivatePublicKeyPaths => new()
    {
        TestJwtTokenFactory.PrivatePkcs1KeyPath,
        TestJwtTokenFactory.PrivatePkcs8KeyPath,
    };

    [Fact]
    public async Task ReadinessConfirmsYarpConfigurationWasLoaded()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/ready");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var checks = document.RootElement.GetProperty("checks").EnumerateArray().ToArray();

        Assert.Contains(checks, check =>
            check.GetProperty("name").GetString() == "yarp-configuration"
            && check.GetProperty("status").GetString() == "Healthy");
    }

    [Fact]
    public async Task ReadinessReportsTheSpecificYarpRegistrationAsUnhealthyForUnknownCluster()
    {
        await using var invalidFactory = factory.WithWebHostBuilder(builder => builder.UseSetting(
            "ReverseProxy:Routes:control-plane:ClusterId",
            "missing-cluster"));
        using var client = invalidFactory.CreateClient();

        using var response = await client.GetAsync("/health/ready");

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var checks = document.RootElement.GetProperty("checks").EnumerateArray().ToArray();
        Assert.Contains(checks, check =>
            check.GetProperty("name").GetString() == "yarp-configuration"
            && check.GetProperty("status").GetString() == "Unhealthy");
    }

    [Fact]
    public void YarpContainsAllPublicServiceRoutes()
    {
        var provider = factory.Services.GetRequiredService<IProxyConfigProvider>();
        var routeIds = provider.GetConfig().Routes.Select(route => route.RouteId).ToHashSet();

        Assert.Contains("identity", routeIds);
        Assert.Contains("identity-login", routeIds);
        Assert.Contains("identity-refresh", routeIds);
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
                options.AccessTokenProvider = () => Task.FromResult<string?>(TestJwtTokenFactory.Issue());
            })
            .Build();

        await connection.StartAsync();
        var pong = await connection.InvokeAsync<TechnicalPong>("Ping");

        Assert.Equal("gateway", pong.Service);
        Assert.Equal(HubConnectionState.Connected, connection.State);
    }

    [Fact]
    public async Task ProtectedProxyRouteRejectsAnonymousRequest()
    {
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/control-plane/service-info");
        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task JwtWithUnknownKeyIdIsRejectedBeforeProxying()
    {
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new(
            "Bearer",
            TestJwtTokenFactory.Issue(keyId: "unknown-key"));

        using var response = await client.GetAsync("/api/control-plane/service-info");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData(InvalidJwtCase.WrongIssuer)]
    [InlineData(InvalidJwtCase.WrongAudience)]
    [InlineData(InvalidJwtCase.WrongAlgorithm)]
    [InlineData(InvalidJwtCase.WrongTokenUse)]
    [InlineData(InvalidJwtCase.MissingTokenUse)]
    public async Task InvalidJwtBoundaryIsRejectedBeforeProxying(InvalidJwtCase invalidCase)
    {
        var token = invalidCase switch
        {
            InvalidJwtCase.WrongIssuer => TestJwtTokenFactory.Issue(issuer: "unexpected-issuer"),
            InvalidJwtCase.WrongAudience => TestJwtTokenFactory.Issue(audience: "unexpected-audience"),
            InvalidJwtCase.WrongAlgorithm => TestJwtTokenFactory.Issue(
                algorithm: Microsoft.IdentityModel.Tokens.SecurityAlgorithms.RsaSsaPssSha256),
            InvalidJwtCase.WrongTokenUse => TestJwtTokenFactory.Issue(tokenUse: "refresh"),
            InvalidJwtCase.MissingTokenUse => TestJwtTokenFactory.Issue(tokenUse: null),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidCase)),
        };
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        using var response = await client.GetAsync("/api/control-plane/service-info");

        Assert.Equal(System.Net.HttpStatusCode.Unauthorized, response.StatusCode);
    }

    public enum InvalidJwtCase
    {
        WrongIssuer,
        WrongAudience,
        WrongAlgorithm,
        WrongTokenUse,
        MissingTokenUse,
    }

    [Fact]
    public async Task SignalRRejectsAnonymousConnection()
    {
        await using var connection = new HubConnectionBuilder()
            .WithUrl("http://localhost/hubs/system", options =>
            {
                options.Transports = HttpTransportType.LongPolling;
                options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
            })
            .Build();

        await Assert.ThrowsAnyAsync<Exception>(() => connection.StartAsync());
    }

    private static async Task AssertStartupFailsAsync(
        WebApplicationFactory<GatewayMarker> invalidFactory,
        string expectedReason)
    {
        var exception = await Record.ExceptionAsync(async () =>
        {
            using var client = invalidFactory.CreateClient();
            using var response = await client.GetAsync("/health/live");
        });
        var validation = FindOptionsValidationException(exception);

        Assert.True(validation is not null, exception?.ToString());
        Assert.Equal(typeof(PlatformAuthenticationOptions), validation.OptionsType);
        Assert.Contains(validation.Failures, failure =>
            failure.Contains(expectedReason, StringComparison.OrdinalIgnoreCase));
    }

    private static OptionsValidationException? FindOptionsValidationException(Exception? exception)
    {
        if (exception is null)
        {
            return null;
        }

        if (exception is OptionsValidationException validation)
        {
            return validation;
        }

        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions
                .Select(FindOptionsValidationException)
                .FirstOrDefault(candidate => candidate is not null);
        }

        return FindOptionsValidationException(exception.InnerException);
    }
}
