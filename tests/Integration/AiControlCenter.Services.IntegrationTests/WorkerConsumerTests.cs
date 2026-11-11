using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AiControlCenter.Contracts.V1;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Worker.Service;
using AiControlCenter.Worker.Service.Messaging;
using Grpc.Core;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class WorkerConsumerTests
{
    private const string WorkerKey = "worker-consumer-test-key-32-characters";

    [Fact]
    public void ExecutionPlanPreservesInitialRoutesAndRetriesWithinRemainingRunBudgetOnResume()
    {
        string[] configuredPlans = ["primary", "fallback"];

        Assert.Equal(configuredPlans, ExecutionPlan.Build(configuredPlans, 3, 0));
        Assert.Equal(["primary", "fallback"], ExecutionPlan.Build(configuredPlans, 3, 1));
        Assert.Equal(["primary"], ExecutionPlan.Build(configuredPlans, 3, 2));
        Assert.Empty(ExecutionPlan.Build(configuredPlans, 3, 3));
    }

    [Theory]
    [InlineData(TestRunOutcomeV1.Succeed, 8, RunCompletionStatus.Succeeded)]
    [InlineData(TestRunOutcomeV1.Fail, 6, RunCompletionStatus.Failed)]
    public async Task ProductionConsumerExecutesOnlyDeterministicGrpcWorkflow(
        TestRunOutcomeV1 expectedOutcome,
        int expectedCallCount,
        RunCompletionStatus completionStatus)
    {
        var invoker = new RecordingCallInvoker();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IOptions<WorkerExecutionOptions>>(Options.Create(new WorkerExecutionOptions
            {
                Address = "http://orchestrator.test",
                ApiKey = WorkerKey,
                DeadlineSeconds = 5,
            }))
            .AddSingleton(new RunProgress.RunProgressClient(invoker))
            .AddMassTransitTestHarness(configurator => configurator.AddConsumer<TestAgentRunConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var message = new ExecuteTestAgentRunV1(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Test Agent", "test-agent", 1,
                Guid.NewGuid(), "Direction", "direction", "bounded input", expectedOutcome, "correlation");
            await harness.Bus.Publish(message);
            Assert.True(await harness.Consumed.Any<ExecuteTestAgentRunV1>());
            Assert.True(await harness.GetConsumerHarness<TestAgentRunConsumer>().Consumed.Any<ExecuteTestAgentRunV1>());
            await invoker.WaitForCallsAsync(expectedCallCount, TimeSpan.FromSeconds(10));
            var calls = invoker.Calls.ToArray();
            Assert.Equal(expectedCallCount, calls.Length);
            Assert.IsType<BeginRunRequest>(calls[0].Request);
            var completion = Assert.IsType<CompleteRunRequest>(calls[^1].Request);
            Assert.Equal(completionStatus, completion.Status);
            Assert.All(calls, call => Assert.Equal(WorkerKey, call.ApiKey));
            Assert.All(calls, call => Assert.True(call.Deadline > DateTime.UtcNow));
            Assert.DoesNotContain(calls, call => call.Request is not (BeginRunRequest or ReportStepRequest or CompleteRunRequest));
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task AiResumeCarriesPersistedToolAuditAndDoesNotLoseCheckpointAcrossGrpcAndRabbitMq()
    {
        var invoker = new AiRecordingCallInvoker();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IOptions<WorkerExecutionOptions>>(Options.Create(new WorkerExecutionOptions
            {
                Address = "http://orchestrator.test",
                ApiKey = WorkerKey,
                DeadlineSeconds = 5,
            }))
            .AddSingleton<IOptions<IntegrationsExecutionOptions>>(Options.Create(new IntegrationsExecutionOptions
            {
                Address = "http://integrations.test",
                ApiKey = WorkerKey,
                DeadlineSeconds = 5,
            }))
            .AddSingleton(new RunProgress.RunProgressClient(invoker))
            .AddSingleton(new AiProviderExecution.AiProviderExecutionClient(invoker))
            .AddMassTransitTestHarness(configurator => configurator.AddConsumer<AiAgentRunConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var runId = Guid.NewGuid();
            var owner = Guid.NewGuid();
            var route = $$"""[{"ownerUserId":"{{owner}}","connectionId":"{{Guid.NewGuid()}}","provider":"DeterministicTest","modelId":"deterministic-v1","priority":1,"allowedFallbackReasons":["QuotaExceeded"],"maxAttempts":1,"connectionAvailable":true,"isPaid":false,"pricingKnown":true}]""";
            var first = Job(runId, owner, route, string.Empty, 0);
            await harness.Bus.Publish(first);
            var firstAttempt = await invoker.FirstAttempt.Task.WaitAsync(TimeSpan.FromSeconds(10));
            _ = await invoker.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Single(firstAttempt.ToolInvocations);
            Assert.False(string.IsNullOrWhiteSpace(firstAttempt.CheckpointJson));
            using (var checkpoint = JsonDocument.Parse(firstAttempt.CheckpointJson))
            {
                var attemptReference = Assert.Single(
                    checkpoint.RootElement.GetProperty("providerAttemptReferences").EnumerateArray());
                Assert.Equal(1, attemptReference.GetProperty("Sequence").GetInt32());
                Assert.Equal("QuotaExceeded", attemptReference.GetProperty("Status").GetString());
                Assert.Equal(1, checkpoint.RootElement.GetProperty("resumeCursor").GetInt32());
            }

            var resumed = Job(runId, owner, route, firstAttempt.CheckpointJson, 1);
            await harness.Bus.Publish(resumed);
            _ = await invoker.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            var providerRequests = invoker.ProviderRequests.ToArray();
            Assert.Equal(2, providerRequests.Length);
            Assert.InRange(providerRequests[0].CheckpointToolAuditBudgetBytes, 1,
                CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes);
            Assert.InRange(providerRequests[1].CheckpointToolAuditBudgetBytes, 1,
                providerRequests[0].CheckpointToolAuditBudgetBytes - 1);
            Assert.Empty(providerRequests[0].PriorToolResults);
            var prior = Assert.Single(providerRequests[1].PriorToolResults);
            Assert.Equal("call-1", prior.CallId);
            Assert.Equal("safe-result", prior.Output);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public void CheckpointEnvelopeBudgetIncludesRouteAttemptsMetadataAndFailsClosed()
    {
        var routeSnapshot = "[{\"provider\":\"OpenAi\",\"modelId\":\"gpt-5-mini\","
            + "\"priority\":1,\"scenario\":\"" + new string('ж', 7_000) + "\"}]";
        var attemptReferences = JsonSerializer.Serialize(Enumerable.Range(1, 9).Select(index => new
        {
            Sequence = index,
            ProviderRequestId = new string('r', 256),
            ActualModel = new string('m', 128),
            Status = new string('s', 64),
        }));
        var timestamp = DateTimeOffset.MaxValue;

        var budget = CheckpointEnvelopeBudget.CalculateToolAuditBudget(
            "вхід", routeSnapshot, attemptReferences, 10, 10, timestamp);
        var expected = CalculateExpectedWorstCaseMetadataBudget(
            "вхід", routeSnapshot, attemptReferences, 10, 10, timestamp);

        Assert.True(Encoding.UTF8.GetByteCount(routeSnapshot) > routeSnapshot.Length);
        Assert.Equal(expected.Budget, budget);
        Assert.Equal(CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes,
            expected.EnvelopeBytes + budget);
        Assert.Equal(10, JsonNode.Parse(attemptReferences)!.AsArray().Count
            + ProviderCheckpointMetadataContract.MaximumNewAttemptReferencesPerCheckpoint);
        Assert.InRange(budget, 1, CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes - 1);
        Assert.Equal(0, CheckpointEnvelopeBudget.CalculateToolAuditBudget(
            "input", "not-json", attemptReferences, 11, 11, DateTimeOffset.MaxValue));
        Assert.Equal(0, CheckpointEnvelopeBudget.CalculateToolAuditBudget(
            "input", routeSnapshot, attemptReferences, 0, 11, DateTimeOffset.MaxValue));
    }

    private static (int Budget, int EnvelopeBytes) CalculateExpectedWorstCaseMetadataBudget(
        string confirmedContext,
        string routeSnapshotJson,
        string providerAttemptReferencesJson,
        int checkpointVersion,
        int resumeCursor,
        DateTimeOffset timestamp)
    {
        var attempts = JsonNode.Parse(providerAttemptReferencesJson)!.AsArray();
        attempts.Add(new JsonObject
        {
            ["Sequence"] = int.MaxValue,
            ["ProviderRequestId"] = new string('r', 256),
            ["ActualModel"] = new string('m', 128),
            ["Status"] = new string('s', 64),
        });
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["checkpointVersion"] = checkpointVersion,
            ["logicalStepId"] = "ai-execution",
            ["confirmedContext"] = confirmedContext,
            ["partialOutput"] = null,
            ["partialOutputIsComplete"] = false,
            ["completedToolCalls"] = new JsonArray(),
            ["providerAttemptReferences"] = attempts,
            ["resumeCursor"] = resumeCursor,
            ["lastSafeTimestamp"] = timestamp.ToString("O"),
            ["routeSnapshot"] = JsonNode.Parse(routeSnapshotJson),
        };
        var payload = root.ToJsonString();
        root["hash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();
        var envelopeBytes = Encoding.UTF8.GetByteCount(root.ToJsonString());
        return (CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes - envelopeBytes, envelopeBytes);
    }

    [Fact]
    public async Task OversizedToolAuditNeverSendsOversizedCheckpointAndRunTerminates()
    {
        var invoker = new OversizedAuditCallInvoker();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IOptions<WorkerExecutionOptions>>(Options.Create(new WorkerExecutionOptions
            {
                Address = "http://orchestrator.test",
                ApiKey = WorkerKey,
                DeadlineSeconds = 5,
            }))
            .AddSingleton<IOptions<IntegrationsExecutionOptions>>(Options.Create(new IntegrationsExecutionOptions
            {
                Address = "http://integrations.test",
                ApiKey = WorkerKey,
                DeadlineSeconds = 5,
            }))
            .AddSingleton(new RunProgress.RunProgressClient(invoker))
            .AddSingleton(new AiProviderExecution.AiProviderExecutionClient(invoker))
            .AddMassTransitTestHarness(configurator => configurator.AddConsumer<AiAgentRunConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var owner = Guid.NewGuid();
            var route = $$"""[{"ownerUserId":"{{owner}}","connectionId":"{{Guid.NewGuid()}}","provider":"DeterministicTest","modelId":"deterministic-v1","priority":1,"maxAttempts":1,"connectionAvailable":true,"isPaid":false,"pricingKnown":true}]""";
            await harness.Bus.Publish(Job(Guid.NewGuid(), owner, route, string.Empty, 0));
            var completion = await invoker.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(RunCompletionStatus.Failed, completion.Status);
            Assert.Equal(1, invoker.ProviderCallCount);
            Assert.DoesNotContain(invoker.Attempts, attempt =>
                Encoding.UTF8.GetByteCount(attempt.CheckpointJson) > 65_536);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task AmbiguousPaidAttemptNeverStartsFallbackRoute()
    {
        var invoker = new AmbiguousAttemptCallInvoker();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IOptions<WorkerExecutionOptions>>(Options.Create(new WorkerExecutionOptions
            {
                Address = "http://orchestrator.test",
                ApiKey = WorkerKey,
                DeadlineSeconds = 5,
            }))
            .AddSingleton<IOptions<IntegrationsExecutionOptions>>(Options.Create(new IntegrationsExecutionOptions
            {
                Address = "http://integrations.test",
                ApiKey = WorkerKey,
                DeadlineSeconds = 5,
            }))
            .AddSingleton(new RunProgress.RunProgressClient(invoker))
            .AddSingleton(new AiProviderExecution.AiProviderExecutionClient(invoker))
            .AddMassTransitTestHarness(configurator => configurator.AddConsumer<AiAgentRunConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var owner = Guid.NewGuid();
            var routes = $$"""[{"ownerUserId":"{{owner}}","connectionId":"{{Guid.NewGuid()}}","provider":"OpenAi","modelId":"gpt-5-mini","priority":1,"allowedFallbackReasons":["AmbiguousTimeout"],"maxAttempts":1,"paidExecutionAllowed":true,"paidFallbackAllowed":true,"safetyCapUsd":1,"connectionAvailable":true,"isPaid":true,"pricingKnown":true},{"ownerUserId":"{{owner}}","connectionId":"{{Guid.NewGuid()}}","provider":"XAi","modelId":"grok-code-fast","priority":2,"allowedFallbackReasons":["AmbiguousTimeout"],"maxAttempts":1,"paidExecutionAllowed":true,"paidFallbackAllowed":true,"safetyCapUsd":1,"connectionAvailable":true,"isPaid":true,"pricingKnown":true}]""";
            await harness.Bus.Publish(new ExecuteAiAgentRunV1(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), owner, "AI Agent", "input", "system",
                routes, "[]", 100, 100, 2, 2, 0, 0, string.Empty, "correlation"));
            var completion = await invoker.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var step = await invoker.FailedStep.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(RunCompletionStatus.Failed, completion.Status);
            Assert.Equal(RunStepProgressStatus.Failed, step.Status);
            Assert.Contains("outcome is unknown", completion.Error, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, invoker.ProviderCallCount);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task PredispatchConfigurationFailureReportsTypedAttemptAndTerminatesStep()
    {
        var invoker = new ConfigurationFailureCallInvoker();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IOptions<WorkerExecutionOptions>>(Options.Create(new WorkerExecutionOptions
            {
                Address = "http://orchestrator.test",
                ApiKey = WorkerKey,
                DeadlineSeconds = 5,
            }))
            .AddSingleton<IOptions<IntegrationsExecutionOptions>>(Options.Create(new IntegrationsExecutionOptions
            {
                Address = "http://integrations.test",
                ApiKey = WorkerKey,
                DeadlineSeconds = 5,
            }))
            .AddSingleton(new RunProgress.RunProgressClient(invoker))
            .AddSingleton(new AiProviderExecution.AiProviderExecutionClient(invoker))
            .AddMassTransitTestHarness(configurator => configurator.AddConsumer<AiAgentRunConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var owner = Guid.NewGuid();
            var route = $$"""[{"ownerUserId":"{{owner}}","connectionId":"{{Guid.NewGuid()}}","provider":"GitHubCopilotLocalCli","modelId":"Auto","priority":1,"allowedFallbackReasons":[],"maxAttempts":1,"connectionAvailable":true,"isPaid":false,"pricingKnown":true}]""";
            await harness.Bus.Publish(Job(Guid.NewGuid(), owner, route, string.Empty, 0));

            var attempt = await invoker.Attempt.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var step = await invoker.FailedStep.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var completion = await invoker.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("session_create_failed", attempt.ErrorCode);
            Assert.Equal("SessionCreate", attempt.Operation);
            Assert.Equal("false", attempt.GenerationDispatched);
            Assert.Equal("ProviderRuntimeException", attempt.SafeExceptionType);
            Assert.Equal(RunStepProgressStatus.Failed, step.Status);
            Assert.NotNull(step.Log);
            Assert.Contains("SessionCreate", step.Log, StringComparison.Ordinal);
            Assert.Equal(RunCompletionStatus.Failed, completion.Status);
            Assert.Equal(1, invoker.ProviderCallCount);
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task AuthenticationRecoveryPausesRunWithCheckpointInsteadOfFailingOrRetrying()
    {
        var invoker = new AuthenticationRecoveryCallInvoker();
        await using var provider = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddSingleton<IOptions<WorkerExecutionOptions>>(Options.Create(new WorkerExecutionOptions
            {
                Address = "http://orchestrator.test",
                ApiKey = WorkerKey,
                DeadlineSeconds = 5,
            }))
            .AddSingleton<IOptions<IntegrationsExecutionOptions>>(Options.Create(new IntegrationsExecutionOptions
            {
                Address = "http://integrations.test",
                ApiKey = WorkerKey,
                DeadlineSeconds = 5,
            }))
            .AddSingleton(new RunProgress.RunProgressClient(invoker))
            .AddSingleton(new AiProviderExecution.AiProviderExecutionClient(invoker))
            .AddMassTransitTestHarness(configurator => configurator.AddConsumer<AiAgentRunConsumer>())
            .BuildServiceProvider(true);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();
        try
        {
            var owner = Guid.NewGuid();
            var routes = $$"""[{"ownerUserId":"{{owner}}","connectionId":"{{Guid.NewGuid()}}","provider":"GitHubCopilotLocalCli","modelId":"Auto","priority":1,"allowedFallbackReasons":[],"maxAttempts":1,"paidExecutionAllowed":false,"paidFallbackAllowed":false,"connectionAvailable":true,"isPaid":false,"pricingKnown":true}]""";
            await harness.Bus.Publish(Job(Guid.NewGuid(), owner, routes, string.Empty, 0));

            var attempt = await invoker.Attempt.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var pause = await invoker.Paused.Task.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal("PausedProviderUnavailable", pause.Status);
            Assert.Contains("authentication recovery", pause.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(attempt.CheckpointJson));
            Assert.Equal("Authentication", attempt.FallbackReason);
            Assert.Equal(1, invoker.ProviderCallCount);
            Assert.Equal(0, invoker.CompleteCallCount);
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static ExecuteAiAgentRunV1 Job(Guid runId, Guid owner, string routes,
        string checkpoint, int attemptOffset) => new(
        Guid.NewGuid(), runId, Guid.NewGuid(), owner, "AI Agent", "input", "system",
        routes, "[\"system.utc-time\"]", 100, 100, 1, 2, 0, attemptOffset,
        checkpoint, "correlation");

    private sealed class RecordingCallInvoker : CallInvoker
    {
        private readonly TaskCompletionSource changed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ConcurrentQueue<RecordedCall> Calls { get; } = new();

        public async Task WaitForCallsAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (Calls.Count < count)
            {
                var remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero) throw new TimeoutException($"Expected {count} gRPC calls, observed {Calls.Count}.");
                await changed.Task.WaitAsync(remaining);
            }
        }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            Calls.Enqueue(new RecordedCall(
                request!,
                options.Headers?.GetValue("x-worker-api-key"),
                options.Deadline));
            changed.TrySetResult();
            object response = new RunProgressReply
            {
                Accepted = true,
                AlreadyApplied = false,
                Revision = Calls.Count + 1,
                Status = request is CompleteRunRequest complete
                    ? complete.Status == RunCompletionStatus.Succeeded ? "Succeeded" : "Failed"
                    : "Running",
            };
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult((TResponse)response),
                Task.FromResult(new Metadata()),
                static () => Status.DefaultSuccess,
                static () => [],
                static () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }

    private sealed record RecordedCall(object Request, string? ApiKey, DateTime? Deadline);

    private sealed class AiRecordingCallInvoker : CallInvoker
    {
        private int executions;
        public ConcurrentQueue<ExecuteAiProviderRequest> ProviderRequests { get; } = new();
        public TaskCompletionSource<ReportAiAttemptRequest> FirstAttempt { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PauseRunRequest> Paused { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CompleteRunRequest> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            object response;
            if (request is ExecuteAiProviderRequest execute)
            {
                ProviderRequests.Enqueue(execute);
                var number = Interlocked.Increment(ref executions);
                response = number == 1 ? new ExecuteAiProviderReply
                {
                    Succeeded = false,
                    FailureKind = "QuotaExceeded",
                    ErrorCode = "quota",
                    CanFallback = true,
                    ActualModel = "deterministic-v1",
                    UsageSource = "ProviderReported",
                    CostSource = "Estimated",
                    FinishStatus = "Paused",
                    ToolInvocations =
                    {
                        new ToolInvocationMessage
                        {
                            CallId = "call-1", ToolName = "system.utc-time", ArgumentsJson = "{}",
                            Succeeded = true, Output = "safe-result", Status = "Succeeded",
                            StartedAt = DateTimeOffset.UtcNow.ToString("O"),
                            CompletedAt = DateTimeOffset.UtcNow.ToString("O"),
                            IdempotencyKey = $"{execute.IdempotencyKey}:tool:call-1",
                        },
                    },
                } : new ExecuteAiProviderReply
                {
                    Succeeded = true,
                    Output = "completed",
                    FailureKind = "None",
                    ActualModel = "deterministic-v1",
                    UsageSource = "ProviderReported",
                    CostSource = "Estimated",
                    FinishStatus = "Completed",
                };
            }
            else
            {
                if (request is ReportAiAttemptRequest report) FirstAttempt.TrySetResult(report);
                if (request is PauseRunRequest pause) Paused.TrySetResult(pause);
                if (request is CompleteRunRequest complete) Completed.TrySetResult(complete);
                response = new RunProgressReply { Accepted = true, Status = "Running", Revision = 2 };
            }
            return new AsyncUnaryCall<TResponse>(Task.FromResult((TResponse)response),
                Task.FromResult(new Metadata()), static () => Status.DefaultSuccess,
                static () => [], static () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }

    private sealed class OversizedAuditCallInvoker : CallInvoker
    {
        private int providerCallCount;
        public int ProviderCallCount => Volatile.Read(ref providerCallCount);
        public ConcurrentQueue<ReportAiAttemptRequest> Attempts { get; } = new();
        public TaskCompletionSource<CompleteRunRequest> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            object response;
            if (request is ExecuteAiProviderRequest execute)
            {
                Interlocked.Increment(ref providerCallCount);
                var reply = new ExecuteAiProviderReply
                {
                    Succeeded = false,
                    FailureKind = "Tool",
                    ErrorCode = "tool_audit_budget_exceeded",
                    CanFallback = false,
                    ActualModel = execute.ModelId,
                    FinishStatus = "ToolFailed",
                    PartialOutput = new string('п', 8_000),
                };
                foreach (var index in Enumerable.Range(0, 12))
                {
                    reply.ToolInvocations.Add(new ToolInvocationMessage
                    {
                        CallId = $"call-{index}",
                        ToolName = "text.length",
                        ArgumentsJson = "{\"text\":\"" + new string('ж', 1_900) + "\"}",
                        Succeeded = true,
                        Output = new string('р', 2_000),
                        Status = "Succeeded",
                        StartedAt = DateTimeOffset.UtcNow.ToString("O"),
                        CompletedAt = DateTimeOffset.UtcNow.ToString("O"),
                        IdempotencyKey = $"{execute.IdempotencyKey}:tool:call-{index}",
                    });
                }
                response = reply;
            }
            else
            {
                if (request is ReportAiAttemptRequest attempt) Attempts.Enqueue(attempt);
                if (request is CompleteRunRequest complete) Completed.TrySetResult(complete);
                response = new RunProgressReply { Accepted = true, Status = "Running", Revision = 2 };
            }
            return new AsyncUnaryCall<TResponse>(Task.FromResult((TResponse)response),
                Task.FromResult(new Metadata()), static () => Status.DefaultSuccess,
                static () => [], static () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options,
            TRequest request) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host,
            CallOptions options) => throw new NotSupportedException();
    }

    private sealed class AmbiguousAttemptCallInvoker : CallInvoker
    {
        private int providerCallCount;
        public int ProviderCallCount => Volatile.Read(ref providerCallCount);
        public TaskCompletionSource<CompleteRunRequest> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ReportStepRequest> FailedStep { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            object response;
            if (request is ExecuteAiProviderRequest execute)
            {
                Interlocked.Increment(ref providerCallCount);
                response = new ExecuteAiProviderReply
                {
                    Succeeded = false,
                    FailureKind = "AmbiguousTimeout",
                    ErrorCode = "provider_generation_outcome_ambiguous",
                    ProviderRequestId = execute.IdempotencyKey,
                    CanFallback = false,
                    ActualModel = execute.ModelId,
                    FinishStatus = "Unknown",
                };
            }
            else
            {
                if (request is ReportStepRequest { Status: RunStepProgressStatus.Failed } step)
                    FailedStep.TrySetResult(step);
                if (request is CompleteRunRequest complete) Completed.TrySetResult(complete);
                response = new RunProgressReply { Accepted = true, Status = "Running", Revision = 2 };
            }
            return new AsyncUnaryCall<TResponse>(Task.FromResult((TResponse)response),
                Task.FromResult(new Metadata()), static () => Status.DefaultSuccess,
                static () => [], static () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options,
            TRequest request) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host,
            CallOptions options) => throw new NotSupportedException();
    }

    private sealed class AuthenticationRecoveryCallInvoker : CallInvoker
    {
        private int providerCallCount;
        private int completeCallCount;
        public int ProviderCallCount => Volatile.Read(ref providerCallCount);
        public int CompleteCallCount => Volatile.Read(ref completeCallCount);
        public TaskCompletionSource<ReportAiAttemptRequest> Attempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<PauseRunRequest> Paused { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            object response;
            if (request is ExecuteAiProviderRequest execute)
            {
                Interlocked.Increment(ref providerCallCount);
                response = new ExecuteAiProviderReply
                {
                    Succeeded = false,
                    FailureKind = "Authentication",
                    ErrorCode = "connection_requires_reconnect",
                    ProviderRequestId = execute.IdempotencyKey,
                    CanFallback = false,
                    ActualModel = execute.ModelId,
                    FinishStatus = "Rejected",
                    UsageSource = "Unavailable",
                    CostSource = "Unavailable",
                };
            }
            else
            {
                if (request is ReportAiAttemptRequest attempt) Attempt.TrySetResult(attempt);
                if (request is PauseRunRequest pause) Paused.TrySetResult(pause);
                if (request is CompleteRunRequest) Interlocked.Increment(ref completeCallCount);
                response = new RunProgressReply { Accepted = true, Status = "Running", Revision = 2 };
            }
            return new AsyncUnaryCall<TResponse>(Task.FromResult((TResponse)response),
                Task.FromResult(new Metadata()), static () => Status.DefaultSuccess,
                static () => [], static () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options,
            TRequest request) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host,
            CallOptions options) => throw new NotSupportedException();
    }

    private sealed class ConfigurationFailureCallInvoker : CallInvoker
    {
        private int providerCallCount;
        public int ProviderCallCount => Volatile.Read(ref providerCallCount);
        public TaskCompletionSource<ReportAiAttemptRequest> Attempt { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<ReportStepRequest> FailedStep { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<CompleteRunRequest> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            object response;
            if (request is ExecuteAiProviderRequest)
            {
                Interlocked.Increment(ref providerCallCount);
                response = new ExecuteAiProviderReply
                {
                    Succeeded = false,
                    FailureKind = "Configuration",
                    ErrorCode = "session_create_failed",
                    CanFallback = false,
                    FinishStatus = "Rejected",
                    UsageSource = "Unavailable",
                    CostSource = "Unavailable",
                    Operation = "SessionCreate",
                    GenerationDispatched = "false",
                    SafeExceptionType = "ProviderRuntimeException",
                };
            }
            else
            {
                if (request is ReportAiAttemptRequest attempt) Attempt.TrySetResult(attempt);
                if (request is ReportStepRequest { Status: RunStepProgressStatus.Failed } step)
                    FailedStep.TrySetResult(step);
                if (request is CompleteRunRequest complete) Completed.TrySetResult(complete);
                response = new RunProgressReply { Accepted = true, Status = "Running", Revision = 2 };
            }
            return new AsyncUnaryCall<TResponse>(Task.FromResult((TResponse)response),
                Task.FromResult(new Metadata()), static () => Status.DefaultSuccess,
                static () => [], static () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method,
            string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host, CallOptions options,
            TRequest request) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method, string? host,
            CallOptions options) => throw new NotSupportedException();
    }
}
