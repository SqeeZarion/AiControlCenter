using System.Collections.Concurrent;
using System.Data.Common;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AiControlCenter.Contracts.V1;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Orchestrator.Api;
using AiControlCenter.Orchestrator.Application;
using AiControlCenter.Orchestrator.Domain;
using AiControlCenter.Orchestrator.Infrastructure.Persistence;
using Grpc.Core;
using Grpc.Net.Client;
using MassTransit;
using MassTransit.Testing;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Testcontainers.PostgreSql;
using Testcontainers.RabbitMq;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class OrchestratorRunTests : IAsyncLifetime
{
    private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
    private const string WorkerKey = "integration-worker-key-at-least-32-characters";
    private static readonly Guid OwnerId = Guid.Parse("12345678-1234-1234-1234-123456789012");
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.5-alpine")
        .WithDatabase("orchestrator_run_tests").WithUsername("postgres").WithPassword("test-superuser-password").Build();
    private readonly RabbitMqContainer rabbit = new RabbitMqBuilder("rabbitmq:4.1.4-management-alpine")
        .WithUsername("stage4_test").WithPassword("stage4-test-password").Build();
    private WebApplicationFactory<OrchestratorApiMarker>? factory;
    private ServiceProvider? probeProvider;
    private ITestHarness? harness;
    private RunCommandRecorder? recorder;
    private readonly RunLockCommandBarrier runLockBarrier = new();

    public async Task InitializeAsync()
    {
        await Task.WhenAll(postgres.StartAsync(), rabbit.StartAsync());
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        recorder = new RunCommandRecorder();
        probeProvider = new ServiceCollection()
            .AddSingleton(recorder)
            .AddMassTransitTestHarness(configurator =>
        {
            configurator.AddConsumer<RunCommandProbe>();
            configurator.AddConsumer<RunStatusProbe>();
            configurator.UsingRabbitMq((context, bus) =>
            {
                bus.Host(new Uri(rabbit.GetConnectionString()));
                bus.ReceiveEndpoint($"stage4-run-probe-{Guid.NewGuid():N}", endpoint => endpoint.ConfigureConsumer<RunCommandProbe>(context));
                bus.ReceiveEndpoint($"stage4-status-probe-{Guid.NewGuid():N}", endpoint => endpoint.ConfigureConsumer<RunStatusProbe>(context));
            });
        }).BuildServiceProvider(true);
        harness = probeProvider.GetRequiredService<ITestHarness>();
        await harness.Start();
        factory = new WebApplicationFactory<OrchestratorApiMarker>().WithWebHostBuilder(builder => builder
            .UseEnvironment("Development")
            .UseSetting("ConnectionStrings:OrchestratorDatabase", postgres.GetConnectionString())
            .UseSetting("Authentication:PublicKeyPath", TestJwtTokenFactory.PublicKeyPath)
            .UseSetting("RabbitMq:Host", rabbit.Hostname)
            .UseSetting("RabbitMq:Port", rabbit.GetMappedPublicPort(5672).ToString(CultureInfo.InvariantCulture))
            .UseSetting("RabbitMq:Username", "stage4_test")
            .UseSetting("RabbitMq:Password", "stage4-test-password")
            .UseSetting("WorkerGrpc:ApiKey", WorkerKey)
            .ConfigureServices(services =>
            {
                services.RemoveAll<DbContextOptions<OrchestratorDbContext>>();
                services.AddDbContext<OrchestratorDbContext>(options => options
                    .UseNpgsql(postgres.GetConnectionString(), npgsql =>
                        npgsql.MigrationsHistoryTable("__EFMigrationsHistory", "orchestrator"))
                    .AddInterceptors(runLockBarrier));
                services.RemoveAll<IAgentCatalogClient>();
                services.AddSingleton<IAgentCatalogClient, RunnableAgentCatalog>();
            }));
    }

    public async Task DisposeAsync()
    {
        if (factory is not null) await factory.DisposeAsync();
        if (harness is not null) await harness.Stop();
        if (probeProvider is not null) await probeProvider.DisposeAsync();
        await Task.WhenAll(postgres.DisposeAsync().AsTask(), rabbit.DisposeAsync().AsTask());
    }

    [Fact]
    public async Task RestCreatesQueuedRunTransactionallyAndPublishesBoundedCommand()
    {
        await ClearAsync();
        using var client = CreateClient("User", OwnerId);
        using var response = await client.PostAsJsonAsync("/v1/runs", new { agentId = RunnableAgentCatalog.AgentId, input = "safe input", expectedOutcome = "Succeed" });
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        var created = (await response.Content.ReadFromJsonAsync<AgentRunDto>(WebJson))!;
        Assert.Equal(AgentRunStatus.Queued, created.Status);
        Assert.Equal(OwnerId, created.OwnerUserId);
        Assert.DoesNotContain("Bearer", JsonSerializer.Serialize(created), StringComparison.OrdinalIgnoreCase);
        var consumed = await recorder!.WaitForRunAsync(created.Id);
        Assert.Equal("safe input", consumed.Input);
        Assert.Equal("test-agent", consumed.AgentCode);
        Assert.DoesNotContain("Bearer", JsonSerializer.Serialize(consumed), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, await ScalarAsync<long>("SELECT count(*) FROM orchestrator.\"OutboxMessage\""));
    }

    [Fact]
    public async Task ProductionGrpcAndUnitOfWorkSerializeDuplicateProgressAndPersistJournal()
    {
        await ClearAsync();
        using var client = CreateClient("User");
        var response = await client.PostAsJsonAsync("/v1/runs", new { agentId = RunnableAgentCatalog.AgentId, input = "input", expectedOutcome = "Succeed" });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var created = (await response.Content.ReadFromJsonAsync<AgentRunDto>(WebJson))!;
        var message = await recorder!.WaitForRunAsync(created.Id);
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = factory!.Server.CreateHandler() });
        var progress = new RunProgress.RunProgressClient(channel);
        var headers = new Metadata { { "x-worker-api-key", WorkerKey } };
        var beginRequest = new BeginRunRequest { RunId = created.Id.ToString(), MessageId = message.MessageId.ToString() };
        runLockBarrier.Arm(2);
        var begins = await Task.WhenAll(
            progress.BeginRunAsync(beginRequest, headers).ResponseAsync,
            progress.BeginRunAsync(beginRequest, headers).ResponseAsync).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(runLockBarrier.HitCount >= 2);
        Assert.Single(begins, result => !result.AlreadyApplied);
        Assert.Single(begins, result => result.AlreadyApplied);
        await progress.ReportStepAsync(new ReportStepRequest { RunId = created.Id.ToString(), MessageId = message.MessageId.ToString(), Sequence = 1, Name = "Перевірка", Status = RunStepProgressStatus.Running, Log = "started" }, headers);
        await progress.ReportStepAsync(new ReportStepRequest { RunId = created.Id.ToString(), MessageId = message.MessageId.ToString(), Sequence = 1, Name = "Перевірка", Status = RunStepProgressStatus.Succeeded, Log = "done" }, headers);
        await progress.CompleteRunAsync(new CompleteRunRequest { RunId = created.Id.ToString(), MessageId = message.MessageId.ToString(), Status = RunCompletionStatus.Succeeded, Result = "ok" }, headers);
        var persisted = await client.GetFromJsonAsync<AgentRunDto>($"/v1/runs/{created.Id}", WebJson);
        Assert.Equal(AgentRunStatus.Succeeded, persisted!.Status);
        Assert.Equal("done", Assert.Single(persisted.Steps).Log);
        Assert.Equal("ok", persisted.Result);
    }

    [Fact]
    public async Task ProductionRowLocksSerializeParallelStepsAndRejectCrossRunMessageReuse()
    {
        await ClearAsync();
        using var client = CreateClient("User");
        var firstResponse = await client.PostAsJsonAsync(
            "/v1/runs",
            new { agentId = RunnableAgentCatalog.AgentId, input = "first", expectedOutcome = "Succeed" });
        var first = (await firstResponse.Content.ReadFromJsonAsync<AgentRunDto>(WebJson))!;
        var firstMessage = await recorder!.WaitForRunAsync(first.Id);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = factory!.Server.CreateHandler() });
        var progress = new RunProgress.RunProgressClient(channel);
        var headers = new Metadata { { "x-worker-api-key", WorkerKey } };
        await progress.BeginRunAsync(new BeginRunRequest
        {
            RunId = first.Id.ToString(),
            MessageId = firstMessage.MessageId.ToString(),
        }, headers);

        var step = new ReportStepRequest
        {
            RunId = first.Id.ToString(),
            MessageId = firstMessage.MessageId.ToString(),
            Sequence = 1,
            Name = "Parallel",
            Status = RunStepProgressStatus.Running,
        };
        var parallel = await Task.WhenAll(
            progress.ReportStepAsync(step, headers).ResponseAsync,
            progress.ReportStepAsync(step, headers).ResponseAsync).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(parallel, result => !result.AlreadyApplied);
        Assert.Single(parallel, result => result.AlreadyApplied);
        await progress.ReportStepAsync(new ReportStepRequest
        {
            RunId = first.Id.ToString(),
            MessageId = firstMessage.MessageId.ToString(),
            Sequence = 1,
            Name = "Parallel",
            Status = RunStepProgressStatus.Succeeded,
        }, headers);

        var next = progress.ReportStepAsync(new ReportStepRequest
        {
            RunId = first.Id.ToString(),
            MessageId = firstMessage.MessageId.ToString(),
            Sequence = 2,
            Name = "Second",
            Status = RunStepProgressStatus.Running,
        }, headers).ResponseAsync;
        var skipped = progress.ReportStepAsync(new ReportStepRequest
        {
            RunId = first.Id.ToString(),
            MessageId = firstMessage.MessageId.ToString(),
            Sequence = 3,
            Name = "Third",
            Status = RunStepProgressStatus.Running,
        }, headers).ResponseAsync;
        var outcomes = await Task.WhenAll(CaptureAsync(next), CaptureAsync(skipped))
            .WaitAsync(TimeSpan.FromSeconds(20));
        Assert.Single(outcomes, outcome => outcome.Reply is not null);
        Assert.Single(outcomes, outcome => outcome.Error?.StatusCode == StatusCode.FailedPrecondition);

        var secondResponse = await client.PostAsJsonAsync(
            "/v1/runs",
            new { agentId = RunnableAgentCatalog.AgentId, input = "second", expectedOutcome = "Succeed" });
        var second = (await secondResponse.Content.ReadFromJsonAsync<AgentRunDto>(WebJson))!;
        _ = await recorder.WaitForRunAsync(second.Id);
        var reused = await Assert.ThrowsAsync<RpcException>(() => progress.BeginRunAsync(
            new BeginRunRequest
            {
                RunId = second.Id.ToString(),
                MessageId = firstMessage.MessageId.ToString(),
            }, headers).ResponseAsync);
        Assert.Equal(StatusCode.FailedPrecondition, reused.StatusCode);
    }

    [Fact]
    public async Task StepVersusCompletionIsSerializedAndCanBeRetriedWithoutLostUpdates()
    {
        await ClearAsync();
        using var client = CreateClient("User");
        var response = await client.PostAsJsonAsync(
            "/v1/runs",
            new { agentId = RunnableAgentCatalog.AgentId, input = "race", expectedOutcome = "Succeed" });
        var run = (await response.Content.ReadFromJsonAsync<AgentRunDto>(WebJson))!;
        var message = await recorder!.WaitForRunAsync(run.Id);
        using var channel = GrpcChannel.ForAddress(
            "http://localhost",
            new GrpcChannelOptions { HttpHandler = factory!.Server.CreateHandler() });
        var progress = new RunProgress.RunProgressClient(channel);
        var headers = new Metadata { { "x-worker-api-key", WorkerKey } };
        await progress.BeginRunAsync(new BeginRunRequest
        {
            RunId = run.Id.ToString(),
            MessageId = message.MessageId.ToString(),
        }, headers);
        await progress.ReportStepAsync(new ReportStepRequest
        {
            RunId = run.Id.ToString(),
            MessageId = message.MessageId.ToString(),
            Sequence = 1,
            Name = "Race",
            Status = RunStepProgressStatus.Running,
        }, headers);

        var finishStep = progress.ReportStepAsync(new ReportStepRequest
        {
            RunId = run.Id.ToString(),
            MessageId = message.MessageId.ToString(),
            Sequence = 1,
            Name = "Race",
            Status = RunStepProgressStatus.Succeeded,
        }, headers).ResponseAsync;
        var completion = CaptureAsync(progress.CompleteRunAsync(new CompleteRunRequest
        {
            RunId = run.Id.ToString(),
            MessageId = message.MessageId.ToString(),
            Status = RunCompletionStatus.Succeeded,
            Result = "ok",
        }, headers).ResponseAsync);
        await finishStep;
        var completionOutcome = await completion;
        if (completionOutcome.Error is not null)
        {
            Assert.Equal(StatusCode.FailedPrecondition, completionOutcome.Error.StatusCode);
            await progress.CompleteRunAsync(new CompleteRunRequest
            {
                RunId = run.Id.ToString(),
                MessageId = message.MessageId.ToString(),
                Status = RunCompletionStatus.Succeeded,
                Result = "ok",
            }, headers);
        }

        var persisted = await client.GetFromJsonAsync<AgentRunDto>($"/v1/runs/{run.Id}", WebJson);
        Assert.Equal(AgentRunStatus.Succeeded, persisted!.Status);
        Assert.Equal(RunStepStatus.Succeeded, Assert.Single(persisted.Steps).Status);
    }

    [Fact]
    public async Task StaleXminAndTransactionRollbackProtectRunAndOutbox()
    {
        await ClearAsync();
        var now = DateTimeOffset.UtcNow;
        var run = AgentRun.Create(
            Guid.NewGuid(), RunnableAgentCatalog.AgentId, OwnerId, "xmin-test",
            RunnableAgentCatalog.Snapshot, string.Empty, TestRunOutcome.Succeed, now);
        await using (var seed = CreateDbContext())
        {
            seed.AgentRuns.Add(run);
            await seed.SaveChangesAsync();
        }

        await using var firstContext = CreateDbContext();
        await using var staleContext = CreateDbContext();
        var first = await firstContext.AgentRuns.SingleAsync(item => item.Id == run.Id);
        var stale = await staleContext.AgentRuns.SingleAsync(item => item.Id == run.Id);
        first.Begin(Guid.NewGuid(), now.AddSeconds(1));
        await firstContext.SaveChangesAsync();
        stale.Begin(Guid.NewGuid(), now.AddSeconds(1));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => staleContext.SaveChangesAsync());

        var rolledBackId = Guid.NewGuid();
        using (var scope = factory!.Services.CreateScope())
        {
            var repository = scope.ServiceProvider.GetRequiredService<IAgentRunRepository>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IOrchestratorUnitOfWork>();
            var transport = scope.ServiceProvider.GetRequiredService<IRunTransport>();
            await Assert.ThrowsAsync<InvalidOperationException>(() => unitOfWork.ExecuteInTransactionAsync<int>(
                async cancellationToken =>
                {
                    var rolledBack = AgentRun.Create(
                        rolledBackId, RunnableAgentCatalog.AgentId, OwnerId, "rollback-test",
                        RunnableAgentCatalog.Snapshot, string.Empty, TestRunOutcome.Succeed, now);
                    repository.Add(rolledBack);
                    await transport.QueueAsync(rolledBack, Guid.NewGuid(), cancellationToken);
                    await transport.PublishStatusAsync(rolledBack, null, now, cancellationToken);
                    await unitOfWork.SaveChangesAsync(cancellationToken);
                    throw new InvalidOperationException("Force transaction rollback after outbox preparation.");
                }, CancellationToken.None));
        }

        Assert.Equal(0, await ScalarAsync<long>(
            $"SELECT count(*) FROM orchestrator.agent_runs WHERE id='{rolledBackId}'"));
        Assert.Equal(0, await ScalarAsync<long>(
            "SELECT count(*) FROM orchestrator.\"OutboxMessage\""));
    }

    [Fact]
    public async Task ProductionRabbitFaultConsumerMarksTheMatchingRunFailed()
    {
        await ClearAsync();
        using var hostClient = factory!.CreateClient();
        var now = DateTimeOffset.UtcNow;
        var run = AgentRun.Create(
            Guid.NewGuid(), RunnableAgentCatalog.AgentId, OwnerId, "fault-consumer",
            RunnableAgentCatalog.Snapshot, string.Empty, TestRunOutcome.Succeed, now);
        await using (var db = CreateDbContext())
        {
            db.AgentRuns.Add(run);
            await db.SaveChangesAsync();
        }

        var message = new ExecuteTestAgentRunV1(
            Guid.NewGuid(), run.Id, run.AgentId, run.AgentName, run.AgentCode, run.AgentVersion,
            run.DirectionId, run.DirectionName, run.DirectionCode, run.Input,
            TestRunOutcomeV1.Succeed, run.CorrelationId);
        recorder!.FailRun(run.Id);
        using (var scope = probeProvider!.CreateScope())
            await scope.ServiceProvider.GetRequiredService<IPublishEndpoint>().Publish(message);

        var failed = await recorder.WaitForFailedStatusAsync(run.Id);
        Assert.Equal("Failed", failed.Status);
        await using var verification = CreateDbContext();
        var persisted = await verification.AgentRuns.AsNoTracking().SingleAsync(item => item.Id == run.Id);
        Assert.Equal(AgentRunStatus.Failed, persisted.Status);
        Assert.Equal(message.MessageId, persisted.ExecutionMessageId);
        Assert.Equal(
            "Worker could not complete the deterministic test run after bounded retries.",
            persisted.Error);
    }

    [Fact]
    public async Task CompleteVersusFaultIsSerializedWithoutLostTerminalUpdate()
    {
        await ClearAsync();
        using var client = CreateClient("User");
        var response = await client.PostAsJsonAsync(
            "/v1/runs",
            new { agentId = RunnableAgentCatalog.AgentId, input = "terminal-race", expectedOutcome = "Succeed" });
        var run = (await response.Content.ReadFromJsonAsync<AgentRunDto>(WebJson))!;
        var message = await recorder!.WaitForRunAsync(run.Id);
        using var firstScope = factory!.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        var completeService = firstScope.ServiceProvider.GetRequiredService<AgentRunApplicationService>();
        var faultService = secondScope.ServiceProvider.GetRequiredService<AgentRunApplicationService>();
        await completeService.BeginAsync(
            new BeginAgentRunCommand(run.Id, message.MessageId), CancellationToken.None);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        runLockBarrier.Arm(2);

        var completion = CaptureExceptionAsync(async () =>
        {
            await release.Task;
            await completeService.CompleteAsync(
                new CompleteAgentRunCommand(
                    run.Id, message.MessageId, AgentRunStatus.Succeeded, "ok", null),
                CancellationToken.None);
        });
        var fault = CaptureExceptionAsync(async () =>
        {
            await release.Task;
            await faultService.FailAsync(
                new FailAgentRunCommand(run.Id, message.MessageId, "bounded worker failure"),
                CancellationToken.None);
        });
        release.SetResult();
        var outcomes = await Task.WhenAll(completion, fault).WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(runLockBarrier.HitCount >= 2);
        Assert.Single(outcomes, exception => exception is null);
        Assert.Single(outcomes, exception => exception is AgentRunConflictException);
        await using var verification = CreateDbContext();
        var persisted = await verification.AgentRuns.AsNoTracking().SingleAsync(item => item.Id == run.Id);
        Assert.Contains(
            persisted.Status,
            new[] { AgentRunStatus.Succeeded, AgentRunStatus.Failed });
        Assert.Equal(3, persisted.Revision);
        Assert.Equal(persisted.Status == AgentRunStatus.Succeeded, persisted.Result == "ok");
        Assert.Equal(persisted.Status == AgentRunStatus.Failed, persisted.Error == "bounded worker failure");
    }

    [Fact]
    public async Task RunVisibilityAndWorkerAuthenticationAreFailClosed()
    {
        await ClearAsync();
        using var owner = CreateClient("User");
        var response = await owner.PostAsJsonAsync("/v1/runs", new { agentId = RunnableAgentCatalog.AgentId, input = "", expectedOutcome = "Fail" });
        var run = (await response.Content.ReadFromJsonAsync<AgentRunDto>(WebJson))!;
        using var anonymous = factory!.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/v1/runs")).StatusCode);
        using var otherUser = CreateClient("User", Guid.NewGuid());
        using var denied = await otherUser.GetAsync($"/v1/runs/{run.Id}");
        Assert.True(denied.StatusCode == HttpStatusCode.NotFound, await denied.Content.ReadAsStringAsync());
        using var admin = CreateClient("Admin", Guid.NewGuid());
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync($"/v1/runs/{run.Id}")).StatusCode);
        using var channel = GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions { HttpHandler = factory.Server.CreateHandler() });
        var progress = new RunProgress.RunProgressClient(channel);
        var exception = await Assert.ThrowsAsync<RpcException>(() => progress.BeginRunAsync(new BeginRunRequest { RunId = run.Id.ToString(), MessageId = Guid.NewGuid().ToString() }).ResponseAsync);
        Assert.Equal(StatusCode.Unauthenticated, exception.StatusCode);
    }

    [Fact]
    public async Task MissingAgentMapsToNotFoundWhileRunnableAgentIsAccepted()
    {
        await ClearAsync();
        using var client = CreateClient("User", OwnerId);

        using var missing = await client.PostAsJsonAsync(
            "/v1/runs",
            new { agentId = Guid.NewGuid(), input = string.Empty, expectedOutcome = "Succeed" });
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        using var accepted = await client.PostAsJsonAsync(
            "/v1/runs",
            new { agentId = RunnableAgentCatalog.AgentId, input = string.Empty, expectedOutcome = "Succeed" });
        Assert.Equal(HttpStatusCode.Accepted, accepted.StatusCode);
    }

    [Fact]
    public async Task MigrationCreatedOutboxAndDatabaseConstraintsIncludingQueuedFailure()
    {
        Assert.Equal("orchestrator.agent_runs", await ScalarAsync<string?>("SELECT to_regclass('orchestrator.agent_runs')::text"));
        Assert.Equal("orchestrator.\"OutboxMessage\"", await ScalarAsync<string?>("SELECT to_regclass('orchestrator.\"OutboxMessage\"')::text"));
        await using var db = CreateDbContext();
        var failed = AgentRun.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "correlation", RunnableAgentCatalog.Snapshot, "", TestRunOutcome.Fail, DateTimeOffset.UtcNow);
        failed.FailBeforeOrDuringStart(Guid.NewGuid(), "controlled", DateTimeOffset.UtcNow.AddSeconds(1));
        db.AgentRuns.Add(failed);
        await db.SaveChangesAsync();
        Assert.Null(failed.StartedAt);
        Assert.Equal(AgentRunStatus.Failed, failed.Status);
    }

    private HttpClient CreateClient(string role, Guid? userId = null)
    {
        var client = factory!.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestJwtTokenFactory.Issue(userId: userId, role: role));
        return client;
    }
    private OrchestratorDbContext CreateDbContext() => new(new DbContextOptionsBuilder<OrchestratorDbContext>().UseNpgsql(postgres.GetConnectionString(), options => options.MigrationsHistoryTable("__EFMigrationsHistory", "orchestrator")).Options);
    private async Task ClearAsync()
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString()); await connection.OpenAsync();
        await using var command = new NpgsqlCommand("TRUNCATE orchestrator.run_steps, orchestrator.agent_runs, orchestrator.\"OutboxMessage\", orchestrator.\"InboxState\", orchestrator.\"OutboxState\" RESTART IDENTITY", connection); await command.ExecuteNonQueryAsync();
    }
    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString()); await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection); var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private static async Task<(RunProgressReply? Reply, RpcException? Error)> CaptureAsync(
        Task<RunProgressReply> operation)
    {
        try
        {
            return (await operation, null);
        }
        catch (RpcException exception)
        {
            return (null, exception);
        }
    }

    private static async Task<Exception?> CaptureExceptionAsync(Func<Task> operation)
    {
        try
        {
            await operation();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    public sealed class RunCommandProbe(RunCommandRecorder recorder) : IConsumer<ExecuteTestAgentRunV1>
    {
        public Task Consume(ConsumeContext<ExecuteTestAgentRunV1> context)
        {
            recorder.Record(context.Message);
            if (recorder.ShouldFail(context.Message.RunId))
                throw new InvalidOperationException("Generate a production MassTransit fault for this run.");
            return Task.CompletedTask;
        }
    }

    public sealed class RunStatusProbe(RunCommandRecorder recorder) : IConsumer<RunStatusChangedV1>
    {
        public Task Consume(ConsumeContext<RunStatusChangedV1> context)
        {
            recorder.Record(context.Message);
            return Task.CompletedTask;
        }
    }

    public sealed class RunCommandRecorder
    {
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ExecuteTestAgentRunV1>> received = new();
        private readonly ConcurrentDictionary<Guid, byte> failedRuns = new();
        private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RunStatusChangedV1>> failedStatuses = new();

        public void FailRun(Guid runId) => failedRuns[runId] = 0;
        public bool ShouldFail(Guid runId) => failedRuns.ContainsKey(runId);

        public void Record(ExecuteTestAgentRunV1 message) =>
            received.GetOrAdd(message.RunId, static _ => NewCompletion()).TrySetResult(message);

        public async Task<ExecuteTestAgentRunV1> WaitForRunAsync(Guid runId)
        {
            var message = await received.GetOrAdd(runId, static _ => NewCompletion()).Task
                .WaitAsync(TimeSpan.FromSeconds(20));
            Assert.Equal(runId, message.RunId);
            return message;
        }

        public void Record(RunStatusChangedV1 message)
        {
            if (string.Equals(message.Status, "Failed", StringComparison.Ordinal))
                failedStatuses.GetOrAdd(message.RunId, static _ => NewStatusCompletion()).TrySetResult(message);
        }

        public Task<RunStatusChangedV1> WaitForFailedStatusAsync(Guid runId) =>
            failedStatuses.GetOrAdd(runId, static _ => NewStatusCompletion()).Task
                .WaitAsync(TimeSpan.FromSeconds(20));

        private static TaskCompletionSource<ExecuteTestAgentRunV1> NewCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static TaskCompletionSource<RunStatusChangedV1> NewStatusCompletion() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RunLockCommandBarrier : DbCommandInterceptor
    {
        private TaskCompletionSource? release;
        private int participants;
        private int hitCount;

        public int HitCount => Volatile.Read(ref hitCount);

        public void Arm(int expectedParticipants)
        {
            participants = expectedParticipants;
            Interlocked.Exchange(ref hitCount, 0);
            release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            var currentRelease = release;
            if (currentRelease is null ||
                !command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase))
                return result;

            if (Interlocked.Increment(ref hitCount) >= participants)
                currentRelease.TrySetResult();
            await currentRelease.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
            return result;
        }
    }

    private sealed class RunnableAgentCatalog : IAgentCatalogClient
    {
        public static readonly Guid AgentId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        public static readonly AgentSnapshot Snapshot = new("Test Agent", "test-agent", "Deterministic", 2, "Test", Guid.Parse("11111111-2222-3333-4444-555555555555"), "Direction", "direction", 3);
        public Task<AgentSnapshot> GetRunnableAgentAsync(Guid agentId, string delegatedAuthorization, CancellationToken cancellationToken)
        {
            Assert.StartsWith("Bearer ", delegatedAuthorization, StringComparison.OrdinalIgnoreCase);
            if (agentId != AgentId) throw new AgentRunAgentNotFoundException(agentId);
            return Task.FromResult(Snapshot);
        }
    }
}
