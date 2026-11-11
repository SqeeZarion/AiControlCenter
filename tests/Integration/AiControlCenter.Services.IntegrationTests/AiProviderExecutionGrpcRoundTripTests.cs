using System.Collections.Concurrent;
using System.Net;
using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Integrations.Api;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class AiProviderExecutionGrpcRoundTripTests
{
    private const string WorkerKey = "grpc-round-trip-worker-key-32-chars";

    [Fact]
    public async Task ProductionKestrelRoundTripPreservesBudgetAndDefaultsMissingFieldToFailClosed()
    {
        var owner = Guid.NewGuid();
        var connection = ProviderConnection.Create(owner, AiProviderKind.DeterministicTest,
            "Deterministic", null, null, DateTimeOffset.UtcNow);
        var receipts = new ReceiptRepository();
        var provider = new ToolCallingProvider();
        var executor = new RecordingToolExecutor();
        var legacyReceipt = ProviderExecutionReceipt.Start(owner, connection.Id, "legacy-local-cli",
            DateTimeOffset.UtcNow, AiProviderKind.GitHubCopilotLocalCli);
        legacyReceipt.Complete(
            """{"Succeeded":false,"ErrorCode":"local_copilot_cli_unavailable"}""",
            DateTimeOffset.UtcNow, actualRequestCount: 1);
        receipts.Add(legacyReceipt);
        var application = new ProviderConnectionService(new ConnectionRepository(connection), receipts,
            new UnusedBudgetManager(), new UnitOfWork(), new Protector(), [provider], executor,
            new ProviderMetadataCanonicalizer(
                ProviderCheckpointMetadataContract.CanonicalizeProviderRequestId,
                ProviderCheckpointMetadataContract.CanonicalizeActualModel,
                CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes), TimeProvider.System);

        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration[WebHostDefaults.ServerUrlsKey] = string.Empty;
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0,
            listen => listen.Protocols = HttpProtocols.Http2));
        builder.Services.AddGrpc();
        builder.Services.AddSingleton(application);
        builder.Services.AddSingleton<IOptions<InternalGrpcOptions>>(
            Options.Create(new InternalGrpcOptions { ApiKey = WorkerKey }));
        await using var app = builder.Build();
        app.MapGrpcService<AiProviderExecutionGrpcService>();
        await app.StartAsync();
        try
        {
            var addresses = app.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()?.Addresses;
            var address = Assert.Single(addresses!, value =>
                value.StartsWith("http://127.0.0.1:", StringComparison.Ordinal));
            using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
            {
                HttpHandler = new SocketsHttpHandler { EnableMultipleHttp2Connections = true },
            });
            var client = new AiProviderExecution.AiProviderExecutionClient(channel);
            var headers = new Metadata { { "x-worker-api-key", WorkerKey } };

            var safety = await client.InspectResumeSafetyAsync(new InspectResumeSafetyRequest
            {
                OwnerUserId = owner.ToString(),
                Provider = AiProviderKind.GitHubCopilotLocalCli.ToString(),
                IdempotencyKey = legacyReceipt.IdempotencyKey,
            }, headers).ResponseAsync;
            Assert.False(safety.Allowed);
            Assert.Equal("LegacyAmbiguousOutcome", safety.Code);
            Assert.Equal(ProviderExecutionReceiptStatus.Completed, legacyReceipt.Status);

            const int validBudget = 48 * 1024;
            var allowed = await client.ExecuteAsync(Request(owner, connection.Id, "grpc:allowed", validBudget),
                headers).ResponseAsync;

            Assert.True(allowed.Succeeded);
            Assert.Equal(2, provider.ObservedBudgets.Count(value => value == validBudget));
            Assert.Equal(1, executor.CallCount);
            Assert.Equal(2, provider.GenerationCalls["grpc:allowed"]);
            Assert.Equal("GenerationResponse", allowed.Operation);
            Assert.Equal("true", allowed.GenerationDispatched);
            Assert.Contains("GenerationResponse", allowed.OperationDiagnosticsJson,
                StringComparison.Ordinal);

            var blocked = await client.ExecuteAsync(Request(owner, connection.Id, "grpc:blocked", null),
                headers).ResponseAsync;

            Assert.False(blocked.Succeeded);
            Assert.Equal(ProviderFailureKind.Tool.ToString(), blocked.FailureKind);
            Assert.Equal("tool_audit_budget_exceeded", blocked.ErrorCode);
            Assert.Single(provider.ObservedBudgets, value => value == 0);
            Assert.Equal(1, executor.CallCount);
            Assert.Equal(1, provider.GenerationCalls["grpc:blocked"]);
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static ExecuteAiProviderRequest Request(
        Guid owner,
        Guid connection,
        string idempotencyKey,
        int? checkpointBudget)
    {
        var request = new ExecuteAiProviderRequest
        {
            OwnerUserId = owner.ToString(),
            ConnectionId = connection.ToString(),
            ModelId = "deterministic-v1",
            SystemPrompt = "system",
            Input = "input",
            IdempotencyKey = idempotencyKey,
            ExpectedProvider = AiProviderKind.DeterministicTest.ToString(),
            MaxToolCycles = 1,
        };
        request.AllowedTools.Add("system.utc-time");
        if (checkpointBudget is int value) request.CheckpointToolAuditBudgetBytes = value;
        return request;
    }

    private sealed class ToolCallingProvider : IAiProvider
    {
        public AiProviderKind Kind => AiProviderKind.DeterministicTest;
        public ConcurrentBag<int> ObservedBudgets { get; } = [];
        public ConcurrentDictionary<string, int> GenerationCalls { get; } = new();
        public Task<bool> ValidateAsync(string? credential, CancellationToken token) => Task.FromResult(true);
        public Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(string? credential, CancellationToken token) =>
            Task.FromResult<IReadOnlyCollection<ProviderModelDto>>([
                new("deterministic-v1", "Deterministic", null, true, false, 0, 0),
            ]);
        public Task<AiExecutionResult> ExecuteAsync(
            AiExecutionRequest request,
            string? credential,
            CancellationToken token)
        {
            ObservedBudgets.Add(request.CheckpointToolAuditBudgetBytes);
            GenerationCalls.AddOrUpdate(request.IdempotencyKey, 1, static (_, count) => count + 1);
            return request.PriorToolResults is not { Count: > 0 }
                ? Task.FromResult(new AiExecutionResult(false, null, ProviderFailureKind.None, null,
                    "request-1", 1, 1, 0, [], "deterministic-v1", FinishStatus: "ToolCalls",
                    RequestedToolCalls: [new("call-1", "system.utc-time", "{}")]))
                : Task.FromResult(new AiExecutionResult(true, "finished", ProviderFailureKind.None, null,
                    "request-2", 1, 1, 0, [], "deterministic-v1", FinishStatus: "Completed",
                    Operation: "GenerationResponse", GenerationDispatched: "true",
                    OperationDiagnostics:
                    [
                        new("GenerationResponse", "Completed", 1, "completed", null,
                            "correlation-safe", "request-2", "true"),
                    ]));
        }
    }

    private sealed class RecordingToolExecutor : IToolExecutor
    {
        public int CallCount { get; private set; }
        public Task<ToolInvocationResult> ExecuteAsync(
            ToolCallRequest request,
            string idempotencyKey,
            CancellationToken token)
        {
            CallCount++;
            return Task.FromResult(new ToolInvocationResult(request.ToolName, true, "safe", null,
                request.CallId, request.ArgumentsJson, "Succeeded", DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow, idempotencyKey));
        }
    }

    private sealed class ConnectionRepository(ProviderConnection connection) : IProviderConnectionRepository
    {
        public Task<IReadOnlyCollection<ProviderConnection>> ListAsync(Guid ownerUserId, CancellationToken token) =>
            Task.FromResult<IReadOnlyCollection<ProviderConnection>>([connection]);
        public Task<ProviderConnection?> GetAsync(Guid id, Guid ownerUserId, bool tracking, CancellationToken token) =>
            Task.FromResult<ProviderConnection?>(id == connection.Id && ownerUserId == connection.OwnerUserId
                ? connection : null);
        public Task<ProviderConnection?> GetByProviderAsync(Guid ownerUserId, AiProviderKind provider,
            string displayName, CancellationToken token) => Task.FromResult<ProviderConnection?>(null);
        public void Add(ProviderConnection value) => throw new NotSupportedException();
    }

    private sealed class ReceiptRepository : IProviderExecutionReceiptRepository
    {
        private readonly List<ProviderExecutionReceipt> receipts = [];
        public Task<ProviderExecutionReceipt?> GetAsync(Guid ownerUserId, string idempotencyKey,
            CancellationToken token) => Task.FromResult(receipts.SingleOrDefault(receipt =>
                receipt.OwnerUserId == ownerUserId && receipt.IdempotencyKey == idempotencyKey));
        public Task<IReadOnlyCollection<ProviderExecutionReceipt>> ListCompletedAsync(Guid ownerUserId,
            DateTimeOffset since, CancellationToken token) =>
            Task.FromResult<IReadOnlyCollection<ProviderExecutionReceipt>>([]);
        public void Add(ProviderExecutionReceipt receipt) => receipts.Add(receipt);
    }

    private sealed class UnusedBudgetManager : IProviderBudgetManager
    {
        public Task<BudgetReservationResult> ReserveAsync(AiExecutionRequest request, AiProviderKind provider,
            string modelId, DateTimeOffset now, CancellationToken token) => throw new NotSupportedException();
        public Task<BudgetReservationResult> ReserveAdditionalRequestAsync(ProviderExecutionReceipt receipt,
            AiExecutionRequest request, DateTimeOffset now, CancellationToken token) => throw new NotSupportedException();
        public Task ReconcileAsync(ProviderExecutionReceipt receipt, AiExecutionResult result,
            DateTimeOffset now, CancellationToken token) => throw new NotSupportedException();
    }

    private sealed class UnitOfWork : IIntegrationsUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken token) => Task.FromResult(1);
    }

    private sealed class Protector : IProviderCredentialProtector
    {
        public string ProtectCredential(string credential, ProviderCredentialProtectionContext context) => credential;
        public string UnprotectCredential(string credential, ProviderCredentialProtectionContext context) => credential;
        public string ProtectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string UnprotectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string Fingerprint(string credential) => "fingerprint";
    }
}
