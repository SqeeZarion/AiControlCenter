using Microsoft.Extensions.Diagnostics.HealthChecks;
using Yarp.ReverseProxy.Configuration;

namespace AiControlCenter.Gateway;

//перевіряє, чи Gateway правильно налаштований як reverse proxy через YARP (проксі від майкрософт).

//чи існують маршрути;
// чи існують кластери сервісів;
// чи кожен маршрут веде до реального кластера;
// чи кожен кластер має хоча б одну адресу призначення.
public sealed class YarpConfigurationHealthCheck(IProxyConfigProvider proxyConfigProvider) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var configuration = proxyConfigProvider.GetConfig();
        if (configuration.Routes.Count == 0 || configuration.Clusters.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy("YARP routes and clusters are required."));
        }

        var clusters = configuration.Clusters.ToDictionary(cluster => cluster.ClusterId, StringComparer.Ordinal);
        var invalidRoute = configuration.Routes.FirstOrDefault(route =>
            string.IsNullOrWhiteSpace(route.ClusterId) || !clusters.ContainsKey(route.ClusterId));
        if (invalidRoute is not null)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"YARP route '{invalidRoute.RouteId}' references an unknown cluster."));
        }

        var emptyCluster = configuration.Clusters.FirstOrDefault(cluster => cluster.Destinations is null
            || cluster.Destinations.Count == 0);
        return Task.FromResult(emptyCluster is null
            ? HealthCheckResult.Healthy("YARP configuration is loaded.")
            : HealthCheckResult.Unhealthy($"YARP cluster '{emptyCluster.ClusterId}' has no destinations."));
    }
}
