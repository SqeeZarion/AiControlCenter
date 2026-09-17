using System.Collections.Concurrent;
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
}
