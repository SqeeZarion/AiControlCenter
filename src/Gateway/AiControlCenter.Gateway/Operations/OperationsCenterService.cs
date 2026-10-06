using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Gateway.Operations;

public interface IOperationsCenterService
{
    Task<OperationsCenterSnapshotDto> GetSnapshotAsync(
        string delegatedAuthorization,
        CancellationToken cancellationToken);
}

public sealed class OperationsCenterService(
    IHttpClientFactory httpClientFactory,
    HealthCheckService healthChecks,
    IOptions<OperationsCenterOptions> options,
    IConfiguration configuration,
    TimeProvider timeProvider) : IOperationsCenterService
{
    public const string HttpClientName = "operations-center";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly OperationsCenterOptions settings = options.Value;
    private readonly ConcurrentDictionary<string, DateTimeOffset> lastSuccess = new(StringComparer.Ordinal);

    public async Task<OperationsCenterSnapshotDto> GetSnapshotAsync(
        string delegatedAuthorization,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var gatewayTask = ReadGatewayHealthAsync(now, cancellationToken);
        var serviceTasks = settings.Services
            .OrderBy(target => target.Key, StringComparer.Ordinal)
            .Select(target => ReadServiceHealthAsync(target.Key, target.Value, now, cancellationToken))
            .ToArray();
        var rabbitTask = ReadRabbitMqHealthAsync(now, cancellationToken);
        var queueTasks = settings.Queues
            .Select(queue => ReadQueueAsync(queue, now, cancellationToken))
            .ToArray();
        var workloadTask = ReadWorkloadAsync(delegatedAuthorization, cancellationToken);

        var serviceNodes = await Task.WhenAll(serviceTasks);
        var nodes = new List<OperationsNodeDto>(serviceNodes.Length + 5)
        {
            await gatewayTask,
        };
        nodes.AddRange(serviceNodes);
        nodes.Add(CreatePostgresNode(serviceNodes, now));
        nodes.Add(await rabbitTask);

        var workload = await workloadTask;
        nodes.Add(CreateWorkloadNode(
            "agent-definitions",
            "Agent Definitions",
            workload.TotalAgents is not null,
            now));
        nodes.Add(CreateWorkloadNode(
            "active-runs",
            "Active Runs",
            workload.RunningRuns is not null,
            now));

        return new OperationsCenterSnapshotDto(
            now,
            settings.FreshnessSeconds,
            nodes,
            await Task.WhenAll(queueTasks),
            workload);
    }

    private async Task<OperationsNodeDto> ReadGatewayHealthAsync(
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            var report = await healthChecks.CheckHealthAsync(
                registration => !registration.Tags.Contains("live"),
                cancellationToken);
            stopwatch.Stop();
            var state = report.Status == HealthStatus.Healthy
                ? OperationsConnectionState.Connected
                : OperationsConnectionState.Degraded;
            MarkSuccess("gateway", state, checkedAt);
            return new OperationsNodeDto(
                "gateway",
                "Gateway",
                "service",
                state,
                stopwatch.Elapsed.TotalMilliseconds,
                checkedAt,
                GetLastSuccess("gateway"),
                report.Entries.Select(entry => new OperationsHealthCheckDto(
                    entry.Key,
                    entry.Value.Status.ToString(),
                    entry.Value.Duration.TotalMilliseconds)).ToArray());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return DisconnectedNode("gateway", "Gateway", "service", checkedAt);
        }
    }

    private async Task<OperationsNodeDto> ReadServiceHealthAsync(
        string id,
        OperationsServiceTargetOptions target,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var response = await SendAsync(
                new HttpRequestMessage(HttpMethod.Get, new Uri(new Uri(target.Address), "/health/ready")),
                cancellationToken);
            stopwatch.Stop();
            var health = await ReadJsonAsync<HealthEnvelope>(response, cancellationToken);
            var state = response.IsSuccessStatusCode
                && string.Equals(health?.Status, "Healthy", StringComparison.OrdinalIgnoreCase)
                    ? OperationsConnectionState.Connected
                    : OperationsConnectionState.Degraded;
            MarkSuccess(id, state, checkedAt);
            return new OperationsNodeDto(
                id,
                target.Label,
                "service",
                state,
                stopwatch.Elapsed.TotalMilliseconds,
                checkedAt,
                GetLastSuccess(id),
                health?.Checks.Select(check => new OperationsHealthCheckDto(
                    check.Name,
                    check.Status,
                    check.Duration)).ToArray() ?? []);
        }
        catch (Exception exception) when (IsConnectivityFailure(exception, cancellationToken))
        {
            return DisconnectedNode(id, target.Label, "service", checkedAt);
        }
    }

    private async Task<OperationsNodeDto> ReadRabbitMqHealthAsync(
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        const string id = "rabbitmq";
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var request = CreateRabbitRequest("/api/health/checks/ready-to-serve-clients");
            using var response = await SendAsync(request, cancellationToken);
            stopwatch.Stop();
            var state = response.IsSuccessStatusCode
                ? OperationsConnectionState.Connected
                : OperationsConnectionState.Degraded;
            MarkSuccess(id, state, checkedAt);
            return new OperationsNodeDto(
                id,
                "RabbitMQ",
                "broker",
                state,
                stopwatch.Elapsed.TotalMilliseconds,
                checkedAt,
                GetLastSuccess(id),
                []);
        }
        catch (Exception exception) when (IsConnectivityFailure(exception, cancellationToken))
        {
            return DisconnectedNode(id, "RabbitMQ", "broker", checkedAt);
        }
    }

    private async Task<OperationsQueueDto> ReadQueueAsync(
        string queue,
        DateTimeOffset checkedAt,
        CancellationToken cancellationToken)
    {
        var id = $"queue:{queue}";
        try
        {
            using var request = CreateRabbitRequest($"/api/queues/%2F/{Uri.EscapeDataString(queue)}");
            using var response = await SendAsync(request, cancellationToken);
            var metrics = await ReadJsonAsync<RabbitQueueEnvelope>(response, cancellationToken);
            var state = response.IsSuccessStatusCode && metrics is not null
                ? OperationsConnectionState.Connected
                : OperationsConnectionState.Degraded;
            MarkSuccess(id, state, checkedAt);
            return new OperationsQueueDto(
                queue,
                state,
                metrics?.MessagesReady,
                metrics?.MessagesUnacknowledged,
                metrics?.Consumers,
                checkedAt,
                GetLastSuccess(id));
        }
        catch (Exception exception) when (IsConnectivityFailure(exception, cancellationToken))
        {
            return new OperationsQueueDto(
                queue,
                OperationsConnectionState.Disconnected,
                null,
                null,
                null,
                checkedAt,
                GetLastSuccess(id));
        }
    }

    private async Task<OperationsWorkloadDto> ReadWorkloadAsync(
        string authorization,
        CancellationToken cancellationToken)
    {
        if (!authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            return EmptyWorkload();

        var controlPlane = settings.Services.GetValueOrDefault("control-plane")?.Address;
        var orchestrator = settings.Services.GetValueOrDefault("orchestrator")?.Address;
        if (string.IsNullOrWhiteSpace(controlPlane) || string.IsNullOrWhiteSpace(orchestrator))
            return EmptyWorkload();

        var totalAgentsTask = ReadAgentCountAsync(controlPlane, null, authorization, cancellationToken);
        var activeAgentsTask = ReadAgentCountAsync(controlPlane, "Active", authorization, cancellationToken);
        var runsTask = ReadRunsAsync(orchestrator, authorization, cancellationToken);
        await Task.WhenAll(totalAgentsTask, activeAgentsTask, runsTask);

        var runs = await runsTask;
        return new OperationsWorkloadDto(
            await totalAgentsTask,
            await activeAgentsTask,
            runs?.Queued,
            runs?.Running,
            runs?.Succeeded,
            runs?.Failed,
            runs?.RecentRuns.Select(run => new OperationsRunDto(
                run.Id,
                run.AgentName,
                run.DirectionName,
                run.Status,
                run.Revision,
                run.CreatedAt,
                run.StartedAt,
                run.CompletedAt,
                run.ActiveStepSequence,
                run.ActiveStepName,
                run.ActiveStepStatus)).ToArray() ?? []);
    }

    private async Task<int?> ReadAgentCountAsync(
        string address,
        string? status,
        string authorization,
        CancellationToken cancellationToken)
    {
        var suffix = "/v1/agents?includeArchived=false&page=1&pageSize=1";
        if (status is not null) suffix += $"&status={Uri.EscapeDataString(status)}";
        try
        {
            using var request = CreateDelegatedRequest(new Uri(new Uri(address), suffix), authorization);
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return (await ReadJsonAsync<AgentListEnvelope>(response, cancellationToken))?.TotalCount;
        }
        catch (Exception exception) when (IsConnectivityFailure(exception, cancellationToken))
        {
            return null;
        }
    }

    private async Task<RunOperationsEnvelope?> ReadRunsAsync(
        string address,
        string authorization,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = CreateDelegatedRequest(
                new Uri(new Uri(address), "/v1/operations/runs?recentLimit=8"),
                authorization);
            using var response = await SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode) return null;
            return await ReadJsonAsync<RunOperationsEnvelope>(response, cancellationToken);
        }
        catch (Exception exception) when (IsConnectivityFailure(exception, cancellationToken))
        {
            return null;
        }
    }

    private OperationsNodeDto CreatePostgresNode(
        IReadOnlyCollection<OperationsNodeDto> serviceNodes,
        DateTimeOffset checkedAt)
    {
        var checks = serviceNodes
            .SelectMany(node => node.Checks)
            .Where(check => check.Name.EndsWith("database", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var state = checks.Length switch
        {
            0 => OperationsConnectionState.Unknown,
            4 when checks.All(check => string.Equals(
                check.Status,
                "Healthy",
                StringComparison.OrdinalIgnoreCase)) => OperationsConnectionState.Connected,
            _ => OperationsConnectionState.Degraded,
        };
        MarkSuccess("postgres", state, checkedAt);
        return new OperationsNodeDto(
            "postgres",
            "PostgreSQL",
            "database",
            state,
            checks.Length == 0 ? null : checks.Max(check => check.DurationMilliseconds),
            checkedAt,
            GetLastSuccess("postgres"),
            checks);
    }

    private OperationsNodeDto CreateWorkloadNode(
        string id,
        string label,
        bool available,
        DateTimeOffset checkedAt)
    {
        var state = available ? OperationsConnectionState.Connected : OperationsConnectionState.Degraded;
        MarkSuccess(id, state, checkedAt);
        return new OperationsNodeDto(
            id,
            label,
            "workload",
            state,
            null,
            checkedAt,
            GetLastSuccess(id),
            []);
    }

    private HttpRequestMessage CreateRabbitRequest(string path)
    {
        var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(new Uri(settings.RabbitMqManagementAddress), path));
        var username = configuration["RabbitMq:Username"];
        var password = configuration["RabbitMq:Password"];
        if (!string.IsNullOrWhiteSpace(username) && password is not null)
        {
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String(Encoding.UTF8.GetBytes($"{username}:{password}")));
        }
        return request;
    }

    private static HttpRequestMessage CreateDelegatedRequest(Uri uri, string authorization)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);
        return request;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(settings.RequestTimeoutSeconds));
        var client = httpClientFactory.CreateClient(HttpClientName);
        client.MaxResponseContentBufferSize = 1_048_576;
        return await client.SendAsync(
            request,
            HttpCompletionOption.ResponseContentRead,
            timeout.Token);
    }

    private static async Task<T?> ReadJsonAsync<T>(
        HttpResponseMessage response,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength is > 1_048_576) return default;
        try
        {
            await response.Content.LoadIntoBufferAsync(1_048_576, cancellationToken);
        }
        catch (HttpRequestException)
        {
            return default;
        }
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(stream, JsonOptions, cancellationToken);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static bool IsConnectivityFailure(Exception exception, CancellationToken cancellationToken) =>
        exception is HttpRequestException
        || exception is TaskCanceledException && !cancellationToken.IsCancellationRequested
        || exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;

    private void MarkSuccess(string id, OperationsConnectionState state, DateTimeOffset now)
    {
        if (state == OperationsConnectionState.Connected) lastSuccess[id] = now;
    }

    private DateTimeOffset? GetLastSuccess(string id) =>
        lastSuccess.TryGetValue(id, out var value) ? value : null;

    private OperationsNodeDto DisconnectedNode(
        string id,
        string label,
        string kind,
        DateTimeOffset checkedAt) =>
        new(
            id,
            label,
            kind,
            OperationsConnectionState.Disconnected,
            null,
            checkedAt,
            GetLastSuccess(id),
            []);

    private static OperationsWorkloadDto EmptyWorkload() =>
        new(null, null, null, null, null, null, []);

    private sealed record HealthEnvelope(string Status, IReadOnlyCollection<HealthCheckEnvelope> Checks);
    private sealed record HealthCheckEnvelope(string Name, string Status, double Duration);
    private sealed record AgentListEnvelope(int TotalCount);
    private sealed record RabbitQueueEnvelope(
        int MessagesReady,
        int MessagesUnacknowledged,
        int Consumers);
    private sealed record RunOperationsEnvelope(
        int Queued,
        int Running,
        int Succeeded,
        int Failed,
        IReadOnlyCollection<RunOperationsItemEnvelope> RecentRuns);
    private sealed record RunOperationsItemEnvelope(
        Guid Id,
        string AgentName,
        string DirectionName,
        string Status,
        long Revision,
        DateTimeOffset CreatedAt,
        DateTimeOffset? StartedAt,
        DateTimeOffset? CompletedAt,
        int? ActiveStepSequence,
        string? ActiveStepName,
        string? ActiveStepStatus);
}
