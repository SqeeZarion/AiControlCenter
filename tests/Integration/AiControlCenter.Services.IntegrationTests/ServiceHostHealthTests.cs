using AiControlCenter.ControlPlane.Api;
using AiControlCenter.Identity.Api;
using AiControlCenter.Integrations.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class ServiceHostHealthTests
{
    [Fact]
    public Task IdentityLivenessIsHealthy() => AssertLivenessAsync<IdentityApiMarker>(
        "ConnectionStrings:IdentityDatabase");

    [Fact]
    public Task ControlPlaneLivenessIsHealthy() => AssertLivenessAsync<ControlPlaneApiMarker>(
        "ConnectionStrings:ControlPlaneDatabase");

    [Fact]
    public Task IntegrationsLivenessIsHealthy() => AssertLivenessAsync<IntegrationsApiMarker>(
        "ConnectionStrings:IntegrationsDatabase");

    private static async Task AssertLivenessAsync<TEntryPoint>(string connectionStringKey)
        where TEntryPoint : class
    {
        await using var factory = new WebApplicationFactory<TEntryPoint>()
            .WithWebHostBuilder(builder => builder.UseSetting(
                connectionStringKey,
                "Host=127.0.0.1;Port=1;Database=foundation;Username=test;Password=test;Timeout=1"));

        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/health/live");
        response.EnsureSuccessStatusCode();
    }
}
