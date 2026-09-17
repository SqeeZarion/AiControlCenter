using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text.Json;
using AiControlCenter.Contracts.V1;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Worker.Service;
using Grpc.Core;
using MassTransit;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.RabbitMq;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class WorkerRabbitMqTransportTests : IAsyncLifetime
{
    private const string RabbitUser = "stage4_worker";
    private const string RabbitPassword = "stage4-worker-test-password";
    private const string WorkerKey = "worker-rabbit-transport-key-32-characters";
    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4.1.4-management-alpine")
        .WithUsername(RabbitUser)
        .WithPassword(RabbitPassword)
        .Build();
    private readonly ProgressProbe progress = new(WorkerKey);
    private WebApplication? grpcHost;
    private WebApplicationFactory<WorkerServiceMarker>? worker;
    private IBusControl? probeBus;
    private readonly TaskCompletionSource<Fault<ExecuteTestAgentRunV1>> fault =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public async Task InitializeAsync()
    {
        await rabbit.StartAsync();
        grpcHost = await StartGrpcHostAsync(progress);
        var grpcAddress = grpcHost.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        worker = new WebApplicationFactory<WorkerServiceMarker>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .UseSetting("RabbitMq:Host", rabbit.Hostname)
            .UseSetting("RabbitMq:Port", rabbit.GetMappedPublicPort(5672).ToString(CultureInfo.InvariantCulture))
            .UseSetting("RabbitMq:Username", RabbitUser)
            .UseSetting("RabbitMq:Password", RabbitPassword)
            .UseSetting("OrchestratorGrpc:Address", grpcAddress)
            .UseSetting("OrchestratorGrpc:ApiKey", WorkerKey)
            .UseSetting("OrchestratorGrpc:DeadlineSeconds", "5"));
        worker.UseKestrel(0);
        _ = worker.CreateClient();

        probeBus = Bus.Factory.CreateUsingRabbitMq(configuration =>
        {
            configuration.Host(rabbit.Hostname, (ushort)rabbit.GetMappedPublicPort(5672), "/", host =>
            {
                host.Username(RabbitUser);
                host.Password(RabbitPassword);
            });
            configuration.ReceiveEndpoint($"stage4-worker-fault-probe-{Guid.NewGuid():N}", endpoint =>
                endpoint.Handler<Fault<ExecuteTestAgentRunV1>>(context =>
                {
                    fault.TrySetResult(context.Message);
                    return Task.CompletedTask;
                }));
        });
        await probeBus.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (probeBus is not null) await probeBus.StopAsync();
        if (worker is not null) await worker.DisposeAsync();
        if (grpcHost is not null) await grpcHost.DisposeAsync();
        await rabbit.DisposeAsync();
    }

    [Fact]
    public async Task ProductionRabbitTransportCoversSuccessDuplicateRetryAndFault()
    {
        var successful = Message();
        await probeBus!.Publish(successful);
        Assert.Equal("Succeeded", await progress.WaitForTerminalAsync(successful.RunId));
        Assert.Equal(
            ["Begin", "Step:1:Running", "Step:1:Succeeded", "Step:2:Running", "Step:2:Succeeded",
                "Step:3:Running", "Step:3:Succeeded", "Complete:Succeeded"],
            progress.CallsFor(successful.RunId));

        await probeBus.Publish(successful);
        await progress.WaitForBeginCountAsync(successful.RunId, 2);
        Assert.Equal(9, progress.CallsFor(successful.RunId).Length);

        var transient = Message();
        progress.FailBegins(transient.RunId, 2);
        await probeBus.Publish(transient);
        Assert.Equal("Succeeded", await progress.WaitForTerminalAsync(transient.RunId));
        Assert.Equal(3, progress.BeginCount(transient.RunId));

        var poison = Message();
        progress.AlwaysFailBegins(poison.RunId);
        await probeBus.Publish(poison);
        var publishedFault = await fault.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Equal(poison.RunId, publishedFault.Message.RunId);
        Assert.Equal(4, progress.BeginCount(poison.RunId));

        var serialized = JsonSerializer.Serialize(successful);
        Assert.DoesNotContain("Bearer", serialized, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(WorkerKey, serialized, StringComparison.Ordinal);
    }

    private static ExecuteTestAgentRunV1 Message() => new(
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Test Agent", "test-agent", 1,
        Guid.NewGuid(), "Direction", "direction", "bounded input", TestRunOutcomeV1.Succeed,
        Guid.NewGuid().ToString("N"));

    private static async Task<WebApplication> StartGrpcHostAsync(ProgressProbe probe)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0, listen =>
            listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(probe);
        var app = builder.Build();
        app.MapGrpcService<ProgressProbeService>();
        await app.StartAsync();
        return app;
    }

    public sealed class ProgressProbeService(ProgressProbe probe) : RunProgress.RunProgressBase
    {
        public override Task<RunProgressReply> BeginRun(BeginRunRequest request, ServerCallContext context) =>
            probe.BeginAsync(request, context);

        public override Task<RunProgressReply> ReportStep(ReportStepRequest request, ServerCallContext context) =>
            probe.StepAsync(request, context);

        public override Task<RunProgressReply> CompleteRun(CompleteRunRequest request, ServerCallContext context) =>
            probe.CompleteAsync(request, context);
    }

    public sealed class ProgressProbe(string expectedApiKey)
    {
        private readonly ConcurrentDictionary<Guid, ConcurrentQueue<string>> calls = new();
        private readonly ConcurrentDictionary<Guid, string> terminal = new();
        private readonly ConcurrentDictionary<Guid, int> transientFailures = new();
        private readonly ConcurrentDictionary<Guid, byte> permanentFailures = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<string>> completions = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource> beginChanges = new();

        public void FailBegins(Guid runId, int count) => transientFailures[runId] = count;
        public void AlwaysFailBegins(Guid runId) => permanentFailures[runId] = 0;
        public int BeginCount(Guid runId) => CallsFor(runId).Count(call => call == "Begin");
        public string[] CallsFor(Guid runId) => calls.TryGetValue(runId, out var values) ? values.ToArray() : [];

        public async Task<string> WaitForTerminalAsync(Guid runId) =>
            await completions.GetOrAdd(runId, static _ => NewCompletion<string>()).Task
                .WaitAsync(TimeSpan.FromSeconds(20));

        public async Task WaitForBeginCountAsync(Guid runId, int count)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (BeginCount(runId) < count)
            {
                var changed = beginChanges.GetOrAdd(runId, static _ => NewCompletion());
                await changed.Task.WaitAsync(timeout.Token);
                beginChanges.TryRemove(runId, out _);
            }
        }

        public Task<RunProgressReply> BeginAsync(BeginRunRequest request, ServerCallContext context)
        {
            Authenticate(context);
            var runId = Guid.Parse(request.RunId);
            Record(runId, "Begin");
            beginChanges.GetOrAdd(runId, static _ => NewCompletion()).TrySetResult();
            if (permanentFailures.ContainsKey(runId)) throw Unavailable();
            if (transientFailures.TryGetValue(runId, out var remaining) && remaining > 0)
            {
                transientFailures[runId] = remaining - 1;
                throw Unavailable();
            }

            var status = terminal.TryGetValue(runId, out var value) ? value : "Running";
            return Task.FromResult(Reply(status));
        }

        public Task<RunProgressReply> StepAsync(ReportStepRequest request, ServerCallContext context)
        {
            Authenticate(context);
            var runId = Guid.Parse(request.RunId);
            Record(runId, $"Step:{request.Sequence}:{request.Status}");
            return Task.FromResult(Reply("Running"));
        }

        public Task<RunProgressReply> CompleteAsync(CompleteRunRequest request, ServerCallContext context)
        {
            Authenticate(context);
            var runId = Guid.Parse(request.RunId);
            var status = request.Status.ToString();
            Record(runId, $"Complete:{status}");
            terminal[runId] = status;
            completions.GetOrAdd(runId, static _ => NewCompletion<string>()).TrySetResult(status);
            return Task.FromResult(Reply(status));
        }

        private void Authenticate(ServerCallContext context)
        {
            if (!string.Equals(context.RequestHeaders.GetValue("x-worker-api-key"), expectedApiKey, StringComparison.Ordinal))
                throw new RpcException(new Status(StatusCode.Unauthenticated, "Worker authentication failed."));
        }

        private void Record(Guid runId, string call) =>
            calls.GetOrAdd(runId, static _ => new ConcurrentQueue<string>()).Enqueue(call);

        private static RpcException Unavailable() =>
            new(new Status(StatusCode.Unavailable, "Transient test failure."));

        private static RunProgressReply Reply(string status) => new()
        {
            Accepted = true,
            Status = status,
            Revision = 1,
        };

        private static TaskCompletionSource NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource<T> NewCompletion<T>() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
