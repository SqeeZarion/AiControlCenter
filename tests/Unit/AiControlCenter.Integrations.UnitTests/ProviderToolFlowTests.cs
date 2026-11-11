using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using AiControlCenter.Integrations.Infrastructure.Providers;

namespace AiControlCenter.Integrations.UnitTests;

public sealed class ProviderToolFlowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PaidGenerationTransportFailureIsAmbiguousAndKeepsReservation(bool timeout)
    {
        var fixture = CreateTransportFixture(new TransportFailureHandler(
            timeout ? TransportFailureMode.GenerationTimeout : TransportFailureMode.GenerationConnection));

        var result = await fixture.Service.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.AmbiguousTimeout, result.FailureKind);
        Assert.Equal("provider_generation_outcome_ambiguous", result.ErrorCode);
        Assert.False(ProviderFallbackPolicy.CanFallback(result.FailureKind));
        Assert.Equal(ProviderExecutionReceiptStatus.Ambiguous, fixture.Budget.LastReceipt!.Status);
        Assert.Equal(fixture.Request.IdempotencyKey, result.ProviderRequestId);
    }

    [Fact]
    public async Task CatalogTransportFailureRemainsSafeTransientFailure()
    {
        var fixture = CreateTransportFixture(new TransportFailureHandler(TransportFailureMode.CatalogConnection));

        var result = await fixture.Service.ExecuteAsync(fixture.Request, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.TemporarilyUnavailable, result.FailureKind);
        Assert.Equal("provider_catalog_unavailable", result.ErrorCode);
        Assert.True(ProviderFallbackPolicy.CanFallback(result.FailureKind));
        Assert.Equal(ProviderExecutionReceiptStatus.Released, fixture.Budget.LastReceipt!.Status);
    }

    [Fact]
    public async Task AggregateToolAuditBudgetStopsBeforeUnsafeToolExecution()
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(owner, AiProviderKind.OpenAi, "OpenAI",
            "protected", "fingerprint", DateTimeOffset.UtcNow);
        var receipts = new ReceiptRepository();
        var budget = new BudgetManager(receipts);
        var provider = new ManyToolCallsProvider();
        var executor = new RecordingToolExecutor(succeeds: true);
        var service = new ProviderConnectionService(new ConnectionRepository(connection), receipts,
            budget, new UnitOfWork(), new Protector(), [provider], executor, MetadataCanonicalizer(),
            TimeProvider.System);
        var request = new AiExecutionRequest(owner, connection.Id, "gpt-5-mini", "system", "input",
            ["text.length"], null, "run:tool-audit", DailyCostCapUsd: 10, MonthlyCostCapUsd: 100,
            MaxRequestsPerDay: 100, MaxRequestsPerMonth: 100,
            MaxInputTokens: 100, MaxOutputTokens: 100, RunCostCapUsd: 1, RouteCostCapUsd: 1,
            ExpectedProvider: AiProviderKind.OpenAi, MaxToolCycles: 4,
            CheckpointToolAuditBudgetBytes: 48 * 1024);

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.Tool, result.FailureKind);
        Assert.Equal("tool_audit_budget_exceeded", result.ErrorCode);
        Assert.True(executor.CallCount < ManyToolCallsProvider.OfferedCallCount);
        Assert.True(Encoding.UTF8.GetByteCount(System.Text.Json.JsonSerializer.Serialize(result.ToolInvocations))
            <= 65_536);
    }

    [Fact]
    public async Task NearLimitFullCheckpointBudgetStopsToolBeforeExecution()
    {
        var fixture = CreateFixture("system.utc-time", toolSucceeds: true);
        var priorTools = Enumerable.Range(1, 3).Select(index => new ToolInvocationResult(
            "text.length", true, new string('x', 7_000), null, $"prior-{index}", "{}",
            "Succeeded", DateTimeOffset.MaxValue, DateTimeOffset.MaxValue,
            $"run:prior:tool:{index}")).ToArray();
        var checkpoint = CreateNearLimitCheckpoint(priorTools);
        var checkpointBytes = Encoding.UTF8.GetByteCount(checkpoint);
        var remainingBytes = 65_536 - checkpointBytes;

        var result = await fixture.Service.ExecuteAsync(fixture.Request with
        {
            AllowedTools = ["system.utc-time"],
            PriorToolResults = priorTools,
            CheckpointToolAuditBudgetBytes = remainingBytes,
        }, CancellationToken.None);

        Assert.InRange(checkpointBytes, 63_000, 65_535);
        Assert.InRange(remainingBytes, 1, 2_536);
        Assert.Equal(ProviderFailureKind.Tool, result.FailureKind);
        Assert.Equal("tool_audit_budget_exceeded", result.ErrorCode);
        Assert.Equal(0, fixture.Executor.CallCount);
        var duplicate = await fixture.Service.ExecuteAsync(fixture.Request with
        {
            AllowedTools = ["system.utc-time"],
            PriorToolResults = priorTools,
            CheckpointToolAuditBudgetBytes = remainingBytes,
        }, CancellationToken.None);
        Assert.Equal("tool_audit_budget_exceeded", duplicate.ErrorCode);
        Assert.Equal(0, fixture.Executor.CallCount);
        Assert.Equal(1, fixture.Handler.ResponseCallCount);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task OversizedProviderMetadataIsCanonicalizedBeforeReceiptAndTransport(
        bool oversizedRequestId,
        bool oversizedModel)
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(owner, AiProviderKind.OpenAi, "OpenAI",
            "protected", "fingerprint", DateTimeOffset.UtcNow);
        var receipts = new ReceiptRepository();
        var budget = new BudgetManager(receipts);
        var rawRequestId = oversizedRequestId ? new string('r', 10_000) : "request-safe";
        var rawModel = oversizedModel ? new string('m', 10_000) : "model-safe";
        var handler = new OversizedMetadataToolHandler(rawRequestId, rawModel);
        var provider = new OpenAiProvider(new HttpClient(handler)
        {
            BaseAddress = new Uri("https://stub.invalid/"),
        });
        var executor = new RecordingToolExecutor(succeeds: true);
        var service = new ProviderConnectionService(new ConnectionRepository(connection), receipts,
            budget, new UnitOfWork(), new Protector(), [provider], executor, MetadataCanonicalizer(),
            TimeProvider.System);
        var request = new AiExecutionRequest(owner, connection.Id, "gpt-5-mini", "system", "input",
            ["system.utc-time"], null, "run:oversized-metadata", DailyCostCapUsd: 10,
            MonthlyCostCapUsd: 100, MaxRequestsPerDay: 10, MaxRequestsPerMonth: 100,
            MaxInputTokens: 100, MaxOutputTokens: 100, RunCostCapUsd: 1, RouteCostCapUsd: 1,
            ExpectedProvider: AiProviderKind.OpenAi, CheckpointToolAuditBudgetBytes: 48 * 1024);

        var result = await service.ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(1, executor.CallCount);
        Assert.Equal(!oversizedRequestId, rawRequestId == result.ProviderRequestId);
        Assert.Equal(!oversizedModel, rawModel == result.ActualModel);
        Assert.InRange(Encoding.UTF8.GetByteCount(result.ProviderRequestId!), 1, 256);
        Assert.InRange(Encoding.UTF8.GetByteCount(result.ActualModel!), 1, 128);
        if (oversizedRequestId)
            Assert.DoesNotContain(rawRequestId, receipts.SingleResultJson, StringComparison.Ordinal);
        if (oversizedModel)
            Assert.DoesNotContain(rawModel, receipts.SingleResultJson, StringComparison.Ordinal);
    }

    [Fact]
    public void ProviderMetadataContractBoundsEveryTransportAndPersistenceIdentifier()
    {
        const string hostile = "\"\\ж";
        var oversized = string.Concat(Enumerable.Repeat(hostile, 10_000));
        var values = new (string? Value, int Maximum)[]
        {
            (ProviderCheckpointMetadataContract.CanonicalizeProviderRequestId(oversized),
                ProviderCheckpointMetadataContract.ProviderRequestIdMaxUtf8Bytes),
            (ProviderCheckpointMetadataContract.CanonicalizeActualModel(oversized),
                ProviderCheckpointMetadataContract.ActualModelMaxUtf8Bytes),
            (ProviderCheckpointMetadataContract.CanonicalizeProviderName(oversized),
                ProviderCheckpointMetadataContract.ProviderNameMaxUtf8Bytes),
            (ProviderCheckpointMetadataContract.CanonicalizeFailureReason(oversized),
                ProviderCheckpointMetadataContract.FailureReasonMaxUtf8Bytes),
            (ProviderCheckpointMetadataContract.CanonicalizeAttemptStatus(oversized),
                ProviderCheckpointMetadataContract.AttemptStatusMaxUtf8Bytes),
            (ProviderCheckpointMetadataContract.CanonicalizeAttemptRecordStatus(oversized),
                ProviderCheckpointMetadataContract.AttemptRecordStatusMaxUtf8Bytes),
            (ProviderCheckpointMetadataContract.CanonicalizeFinishStatus(oversized),
                ProviderCheckpointMetadataContract.FinishStatusMaxUtf8Bytes),
            (ProviderCheckpointMetadataContract.CanonicalizeMetricSource(oversized),
                ProviderCheckpointMetadataContract.MetricSourceMaxUtf8Bytes),
            (ProviderCheckpointMetadataContract.CanonicalizeCorrelationId(oversized),
                ProviderCheckpointMetadataContract.CorrelationIdMaxUtf8Bytes),
            (ProviderCheckpointMetadataContract.CanonicalizeIdempotencyKey(oversized),
                ProviderCheckpointMetadataContract.IdempotencyKeyMaxUtf8Bytes),
        };

        Assert.All(values, item => Assert.InRange(Encoding.UTF8.GetByteCount(item.Value!), 1, item.Maximum));
        Assert.NotEqual(
            ProviderCheckpointMetadataContract.CanonicalizeProviderRequestId(oversized + "a"),
            ProviderCheckpointMetadataContract.CanonicalizeProviderRequestId(oversized + "b"));
    }

    [Fact]
    public void ToolAuditBudgetHonorsExactByteBoundaryAndFailsClosed()
    {
        var request = new AiExecutionRequest(Guid.NewGuid(), Guid.NewGuid(), "gpt-5-mini",
            "system", "вхід", ["text.length"], null, "run:utf8-boundary",
            ExpectedProvider: AiProviderKind.OpenAi);
        var next = new ToolCallRequest("call-unicode", "text.length",
            "{\"text\":\"жжж\"}");
        const string partialOutput = "частковий результат";
        var requiredBytes = ToolCheckpointAuditSizer.MeasureWorstCaseUtf8Bytes(
            request, [], next, partialOutput);

        Assert.True(Encoding.UTF8.GetByteCount("ж") > "ж".Length);
        Assert.True(ToolCheckpointAuditSizer.Fits(
            request, [], next, partialOutput, requiredBytes,
            CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes));
        Assert.False(ToolCheckpointAuditSizer.Fits(
            request, [], next, partialOutput, requiredBytes - 1,
            CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes));
        Assert.False(ToolCheckpointAuditSizer.Fits(request, [], next, null, 0,
            CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes));
        Assert.False(ToolCheckpointAuditSizer.Fits(request, [], next, null, -1,
            CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes));
        Assert.False(ToolCheckpointAuditSizer.Fits(request, [], next, null, 65_537,
            CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes));
    }

    [Fact]
    public async Task OpenAiToolCallExecutesAllowlistedToolAndReturnsResultToNextModelTurnOnce()
    {
        var fixture = CreateFixture("system.utc-time", toolSucceeds: true);
        var request = fixture.Request with { AllowedTools = ["system.utc-time"] };

        var result = await fixture.Service.ExecuteAsync(request, CancellationToken.None);
        var duplicate = await fixture.Service.ExecuteAsync(request, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal("finished", result.Output);
        var invocation = Assert.Single(result.ToolInvocations);
        Assert.Equal("call-1", invocation.CallId);
        Assert.Equal("system.utc-time", invocation.ToolName);
        Assert.True(invocation.Succeeded);
        Assert.DoesNotContain("provider-secret", invocation.Output, StringComparison.Ordinal);
        Assert.Equal(1, fixture.Executor.CallCount);
        Assert.Equal(result.Output, duplicate.Output);
        Assert.Equal(result.EstimatedCostUsd, duplicate.EstimatedCostUsd);
        Assert.Single(duplicate.ToolInvocations);
        Assert.Equal(2, fixture.Handler.ResponseCallCount);
        Assert.Contains("function_call_output", fixture.Handler.LastRequestBody, StringComparison.Ordinal);
        Assert.Contains("\"tools\"", fixture.Handler.FirstRequestBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ToolAbsentFromAgentSnapshotIsRejectedWithoutExecutionOrPaidFallback()
    {
        var fixture = CreateFixture("system.utc-time", toolSucceeds: true);

        var result = await fixture.Service.ExecuteAsync(fixture.Request with { AllowedTools = [] }, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.Tool, result.FailureKind);
        Assert.False(ProviderFallbackPolicy.CanFallback(result.FailureKind));
        Assert.Equal(0, fixture.Executor.CallCount);
    }

    [Fact]
    public async Task ToolFailureIsAuditedAndNeverStartsPaidFallback()
    {
        var fixture = CreateFixture("system.utc-time", toolSucceeds: false);

        var result = await fixture.Service.ExecuteAsync(fixture.Request with
        {
            AllowedTools = ["system.utc-time"],
        }, CancellationToken.None);

        Assert.Equal(ProviderFailureKind.Tool, result.FailureKind);
        Assert.False(ProviderFallbackPolicy.CanFallback(result.FailureKind));
        Assert.Single(result.ToolInvocations);
        Assert.Equal("safe_tool_failure", result.ToolInvocations.Single().Error);
        Assert.Equal(1, fixture.Executor.CallCount);
    }

    [Fact]
    public async Task ResumeUsesPersistedToolResultAndDoesNotInvokeToolAgain()
    {
        var fixture = CreateFixture("system.utc-time", toolSucceeds: true);
        var persisted = new ToolInvocationResult("system.utc-time", true, "persisted-result", null,
            "call-1", "{}", "Succeeded", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            "run:attempt:1:tool:call-1");

        var result = await fixture.Service.ExecuteAsync(fixture.Request with
        {
            AllowedTools = ["system.utc-time"],
            PriorToolResults = [persisted],
        }, CancellationToken.None);

        Assert.True(result.Succeeded);
        Assert.Equal(0, fixture.Executor.CallCount);
        Assert.Single(result.ToolInvocations);
        Assert.Equal("persisted-result", result.ToolInvocations.Single().Output);
        Assert.Contains("function_call_output", fixture.Handler.LastRequestBody, StringComparison.Ordinal);
    }

    private static Fixture CreateFixture(string requestedTool, bool toolSucceeds)
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(owner, AiProviderKind.OpenAi, "OpenAI",
            "protected", "fingerprint", DateTimeOffset.UtcNow);
        var connections = new ConnectionRepository(connection);
        var receipts = new ReceiptRepository();
        var handler = new ToolHandler(requestedTool);
        var provider = new OpenAiProvider(new HttpClient(handler) { BaseAddress = new Uri("https://stub.invalid/") });
        var executor = new RecordingToolExecutor(toolSucceeds);
        var budget = new BudgetManager(receipts);
        var service = new ProviderConnectionService(connections, receipts, budget, new UnitOfWork(),
            new Protector(), [provider], executor, MetadataCanonicalizer(), TimeProvider.System);
        var request = new AiExecutionRequest(owner, connection.Id, "gpt-5-mini", "system", "input",
            [], null, "run:attempt:1", DailyCostCapUsd: 10, MonthlyCostCapUsd: 100,
            MaxRequestsPerDay: 10, MaxRequestsPerMonth: 100,
            MaxInputTokens: 100, MaxOutputTokens: 100, RunCostCapUsd: 1, RouteCostCapUsd: 1,
            ExpectedProvider: AiProviderKind.OpenAi,
            CheckpointToolAuditBudgetBytes: 48 * 1024);
        return new(service, request, executor, handler, budget);
    }

    private sealed record Fixture(ProviderConnectionService Service, AiExecutionRequest Request,
        RecordingToolExecutor Executor, ToolHandler Handler, BudgetManager Budget);

    private static string CreateNearLimitCheckpoint(
        IReadOnlyCollection<ToolInvocationResult> priorTools)
    {
        var attempts = new JsonArray(Enumerable.Range(1, 10).Select(index => (JsonNode)new JsonObject
        {
            ["Sequence"] = index,
            ["ProviderRequestId"] = new string('ж', 256),
            ["Model"] = new string('ж', 128),
            ["Status"] = "QuotaExceeded",
        }).ToArray());
        var routes = new JsonArray(Enumerable.Range(1, 10).Select(priority => (JsonNode)new JsonObject
        {
            ["ownerUserId"] = Guid.NewGuid(),
            ["connectionId"] = Guid.NewGuid(),
            ["provider"] = "OpenAi",
            ["modelId"] = new string('ж', 128),
            ["priority"] = priority,
            ["enabled"] = true,
            ["scenario"] = new string('ж', 128),
            ["maxAttempts"] = 1,
            ["connectionAvailable"] = true,
            ["isPaid"] = true,
            ["pricingKnown"] = true,
        }).ToArray());
        var root = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["checkpointVersion"] = 10,
            ["logicalStepId"] = "ai-execution",
            ["confirmedContext"] = "input",
            ["partialOutput"] = null,
            ["completedToolCalls"] = System.Text.Json.JsonSerializer.SerializeToNode(priorTools),
            ["providerAttemptReferences"] = attempts,
            ["resumeCursor"] = 10,
            ["lastSafeTimestamp"] = DateTimeOffset.MaxValue.ToString("O"),
            ["routeSnapshot"] = routes,
        };
        var unsigned = root.ToJsonString();
        root["hash"] = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(unsigned))).ToLowerInvariant();
        return root.ToJsonString();
    }

    private static TransportFixture CreateTransportFixture(HttpMessageHandler handler)
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(owner, AiProviderKind.OpenAi, "OpenAI",
            "protected", "fingerprint", DateTimeOffset.UtcNow);
        var receipts = new ReceiptRepository();
        var budget = new BudgetManager(receipts);
        var provider = new OpenAiProvider(new HttpClient(handler) { BaseAddress = new Uri("https://stub.invalid/") });
        var service = new ProviderConnectionService(new ConnectionRepository(connection), receipts,
            budget, new UnitOfWork(), new Protector(), [provider], new RecordingToolExecutor(true),
            MetadataCanonicalizer(), TimeProvider.System);
        var request = new AiExecutionRequest(owner, connection.Id, "gpt-5-mini", "system", "input",
            [], null, "run:transport:1", DailyCostCapUsd: 10, MonthlyCostCapUsd: 100,
            MaxRequestsPerDay: 10, MaxRequestsPerMonth: 100,
            MaxInputTokens: 100, MaxOutputTokens: 100, RunCostCapUsd: 1, RouteCostCapUsd: 1,
            ExpectedProvider: AiProviderKind.OpenAi, MaxToolCycles: 0);
        return new(service, request, budget);
    }

    private sealed record TransportFixture(
        ProviderConnectionService Service,
        AiExecutionRequest Request,
        BudgetManager Budget);

    private static ProviderMetadataCanonicalizer MetadataCanonicalizer() => new(
        ProviderCheckpointMetadataContract.CanonicalizeProviderRequestId,
        ProviderCheckpointMetadataContract.CanonicalizeActualModel,
        CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes);

    private enum TransportFailureMode { GenerationConnection, GenerationTimeout, CatalogConnection }

    private sealed class TransportFailureHandler(TransportFailureMode mode) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                if (mode == TransportFailureMode.CatalogConnection)
                    return Task.FromException<HttpResponseMessage>(new HttpRequestException("catalog unavailable"));
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("{\"data\":[{\"id\":\"gpt-5-mini\"}]}", Encoding.UTF8, "application/json"),
                });
            }
            return mode == TransportFailureMode.GenerationTimeout
                ? Task.FromException<HttpResponseMessage>(new TaskCanceledException("response timeout"))
                : Task.FromException<HttpResponseMessage>(new HttpRequestException("connection closed after dispatch"));
        }
    }

    private sealed class ManyToolCallsProvider : IAiProvider
    {
        private int turn;
        public const int OfferedCallCount = 40;
        public AiProviderKind Kind => AiProviderKind.OpenAi;
        public Task<bool> ValidateAsync(string? credential, CancellationToken token) => Task.FromResult(true);
        public Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(string? credential, CancellationToken token) =>
            Task.FromResult<IReadOnlyCollection<ProviderModelDto>>([
                new("gpt-5-mini", "gpt-5-mini", null, true, false, 0.25m, 2m),
            ]);
        public Task<AiExecutionResult> ExecuteAsync(
            AiExecutionRequest request,
            string? credential,
            CancellationToken token)
        {
            var cycle = Interlocked.Increment(ref turn);
            var calls = Enumerable.Range(0, 10).Select(index => new ToolCallRequest(
                $"call-{cycle}-{index}",
                "text.length",
                "{\"text\":\"" + new string('ж', 1_900) + "\"}"))
                .ToArray();
            return Task.FromResult(new AiExecutionResult(false, null, ProviderFailureKind.None,
                null, null, 1, 1, 0, [], request.ModelId, FinishStatus: "ToolCalls",
                RequestedToolCalls: calls));
        }
    }
    private sealed class ToolHandler(string requestedTool) : HttpMessageHandler
    {
        public int ResponseCallCount { get; private set; }
        public string FirstRequestBody { get; private set; } = string.Empty;
        public string LastRequestBody { get; private set; } = string.Empty;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models")
                return Json("{\"data\":[{\"id\":\"gpt-5-mini\"}]}");
            var body = await request.Content!.ReadAsStringAsync(token);
            ResponseCallCount++;
            if (ResponseCallCount == 1)
            {
                FirstRequestBody = body;
                return Json($"{{\"model\":\"gpt-5-mini\",\"output\":[{{\"type\":\"function_call\",\"call_id\":\"call-1\",\"name\":\"{requestedTool}\",\"arguments\":\"{{}}\"}}],\"usage\":{{\"input_tokens\":5,\"output_tokens\":2}}}}");
            }
            LastRequestBody = body;
            return Json("{\"model\":\"gpt-5-mini\",\"output_text\":\"finished\",\"usage\":{\"input_tokens\":3,\"output_tokens\":1}}");
        }
        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }

    private sealed class OversizedMetadataToolHandler(string requestId, string model) : HttpMessageHandler
    {
        private int responseCallCount;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken token)
        {
            if (request.RequestUri!.AbsolutePath == "/v1/models")
                return Json("{\"data\":[{\"id\":\"gpt-5-mini\"}]}");

            _ = await request.Content!.ReadAsStringAsync(token);
            var response = Interlocked.Increment(ref responseCallCount) == 1
                ? Json($"{{\"model\":\"{model}\",\"output\":[{{\"type\":\"function_call\",\"call_id\":\"call-oversized\",\"name\":\"system.utc-time\",\"arguments\":\"{{}}\"}}],\"usage\":{{\"input_tokens\":5,\"output_tokens\":2}}}}")
                : Json($"{{\"model\":\"{model}\",\"output_text\":\"finished\",\"usage\":{{\"input_tokens\":3,\"output_tokens\":1}}}}");
            response.Headers.TryAddWithoutValidation("x-request-id", requestId);
            return response;
        }

        private static HttpResponseMessage Json(string value) => new(HttpStatusCode.OK)
        { Content = new StringContent(value, Encoding.UTF8, "application/json") };
    }
    private sealed class RecordingToolExecutor(bool succeeds) : IToolExecutor
    {
        public int CallCount { get; private set; }
        public Task<ToolInvocationResult> ExecuteAsync(ToolCallRequest request, string idempotencyKey, CancellationToken token)
        {
            CallCount++;
            return Task.FromResult(new ToolInvocationResult(request.ToolName, succeeds,
                succeeds ? "safe-result" : null, succeeds ? null : "safe_tool_failure",
                request.CallId, request.ArgumentsJson, succeeds ? "Succeeded" : "Failed",
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, idempotencyKey));
        }
    }
    private sealed class BudgetManager(ReceiptRepository receipts) : IProviderBudgetManager
    {
        public ProviderExecutionReceipt? LastReceipt { get; private set; }
        public Task<BudgetReservationResult> ReserveAsync(AiExecutionRequest request, AiProviderKind provider,
            string modelId, DateTimeOffset now, CancellationToken token)
        {
            var receipt = ProviderExecutionReceipt.Start(request.OwnerUserId, request.ConnectionId,
                request.IdempotencyKey, now, provider, 0.01m);
            receipts.Add(receipt);
            LastReceipt = receipt;
            return Task.FromResult(new BudgetReservationResult(true, receipt, null));
        }
        public Task<BudgetReservationResult> ReserveAdditionalRequestAsync(ProviderExecutionReceipt receipt,
            AiExecutionRequest request, DateTimeOffset now, CancellationToken token)
        {
            receipt.ReserveAdditionalRequests(1);
            return Task.FromResult(new BudgetReservationResult(true, receipt, null));
        }
        public Task ReconcileAsync(ProviderExecutionReceipt receipt, AiExecutionResult result,
            DateTimeOffset now, CancellationToken token)
        {
            if (result.FailureKind == ProviderFailureKind.AmbiguousTimeout)
                receipt.MarkAmbiguous(System.Text.Json.JsonSerializer.Serialize(result), now,
                    result.OutboundRequestCount);
            else if (!result.Succeeded)
                receipt.Release(System.Text.Json.JsonSerializer.Serialize(result), now,
                    result.OutboundRequestCount);
            else
                receipt.Complete(System.Text.Json.JsonSerializer.Serialize(result), now,
                    result.EstimatedCostUsd, result.OutboundRequestCount);
            return Task.CompletedTask;
        }
    }
    private sealed class ConnectionRepository(ProviderConnection connection) : IProviderConnectionRepository
    {
        public Task<IReadOnlyCollection<ProviderConnection>> ListAsync(Guid owner, CancellationToken token) =>
            Task.FromResult<IReadOnlyCollection<ProviderConnection>>([connection]);
        public Task<ProviderConnection?> GetAsync(Guid id, Guid owner, bool tracking, CancellationToken token) =>
            Task.FromResult(connection.Id == id && connection.OwnerUserId == owner ? connection : null);
        public Task<ProviderConnection?> GetByProviderAsync(Guid owner, AiProviderKind provider,
            string name, CancellationToken token) => Task.FromResult<ProviderConnection?>(null);
        public void Add(ProviderConnection item) => throw new NotSupportedException();
    }
    private sealed class ReceiptRepository : IProviderExecutionReceiptRepository
    {
        private readonly List<ProviderExecutionReceipt> items = [];
        public string SingleResultJson => Assert.Single(items).ResultJson ?? string.Empty;
        public Task<ProviderExecutionReceipt?> GetAsync(Guid owner, string key, CancellationToken token) =>
            Task.FromResult(items.SingleOrDefault(x => x.OwnerUserId == owner && x.IdempotencyKey == key));
        public Task<IReadOnlyCollection<ProviderExecutionReceipt>> ListCompletedAsync(Guid owner,
            DateTimeOffset since, CancellationToken token) => Task.FromResult<IReadOnlyCollection<ProviderExecutionReceipt>>(
                items.Where(x => x.OwnerUserId == owner && x.Status == ProviderExecutionReceiptStatus.Completed).ToArray());
        public void Add(ProviderExecutionReceipt receipt) => items.Add(receipt);
    }
    private sealed class Protector : IProviderCredentialProtector
    {
        public string ProtectCredential(string credential, ProviderCredentialProtectionContext context) => credential;
        public string UnprotectCredential(string credential, ProviderCredentialProtectionContext context) => "provider-secret";
        public string ProtectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string UnprotectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string Fingerprint(string credential) => "fingerprint";
    }
    private sealed class UnitOfWork : IIntegrationsUnitOfWork
    { public Task<int> SaveChangesAsync(CancellationToken token) => Task.FromResult(1); }
}
