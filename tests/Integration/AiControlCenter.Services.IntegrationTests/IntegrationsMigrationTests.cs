using AiControlCenter.Grpc.Contracts.V1;
using AiControlCenter.Integrations.Application;
using AiControlCenter.Integrations.Domain;
using AiControlCenter.Integrations.Infrastructure.Persistence;
using AiControlCenter.Integrations.Infrastructure.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class IntegrationsMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.5-alpine")
        .WithDatabase("integrations_migration_tests")
        .WithUsername("postgres")
        .WithPassword("test-superuser-password")
        .Build();

    public Task InitializeAsync() => postgres.StartAsync();

    public Task DisposeAsync() => postgres.DisposeAsync().AsTask();

    [Fact]
    public async Task LatestToZeroToLatestPreservesADeployableSchema()
    {
        await using var db = CreateDbContext();

        await db.Database.MigrateAsync();
        await AssertLatestSchemaAsync();

        var migrator = db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("0");
        Assert.Null(await ScalarAsync<string?>("SELECT to_regclass('integrations.provider_connections')::text"));
        Assert.Null(await ScalarAsync<string?>("SELECT to_regclass('integrations.provider_oauth_states')::text"));

        await db.Database.MigrateAsync();
        await AssertLatestSchemaAsync();
    }

    [Fact]
    public async Task ProviderConnectionsAreOwnerScopedAndCredentialColumnStoresOnlyProtectedData()
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        var ownerA = Guid.NewGuid();
        var ownerB = Guid.NewGuid();
        const string rawCredential = "raw-provider-secret-must-not-be-stored";
        var keyDirectory = Directory.CreateTempSubdirectory("aicontrolcenter-integrations-dp-");
        var protector = new ProviderCredentialProtector(DataProtectionProvider.Create(
            keyDirectory, configuration => configuration.SetApplicationName("AiControlCenter.Integrations.Tests")));
        var connectionId = Guid.NewGuid();
        var protectedCredential = protector.ProtectCredential(rawCredential,
            new(AiProviderKind.OpenAi, ownerA, connectionId, 1));
        db.ProviderConnections.Add(ProviderConnection.Create(
            ownerA,
            AiProviderKind.OpenAi,
            "Owner A",
            protectedCredential,
            "sha256:fingerprint",
            DateTimeOffset.UtcNow,
            connectionId));
        await db.SaveChangesAsync();

        var ownerAConnections = await db.ProviderConnections
            .AsNoTracking()
            .Where(connection => connection.OwnerUserId == ownerA)
            .ToArrayAsync();
        var ownerBConnections = await db.ProviderConnections
            .AsNoTracking()
            .Where(connection => connection.OwnerUserId == ownerB)
            .ToArrayAsync();

        var connection = Assert.Single(ownerAConnections);
        Assert.Empty(ownerBConnections);
        Assert.Equal(protectedCredential, connection.ProtectedCredential);
        Assert.NotEqual(rawCredential, connection.ProtectedCredential);
        Assert.DoesNotContain(rawCredential, await ScalarAsync<string>(
            "SELECT protected_credential FROM integrations.provider_connections LIMIT 1"),
            StringComparison.Ordinal);
        keyDirectory.Delete(recursive: true);
    }

    [Fact]
    public void CurrentModelHasNoPendingChanges()
    {
        using var db = CreateDbContext();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task ConcurrentBudgetReservationsAllowOnlyOneRequestSlot()
    {
        await using (var setup = CreateDbContext()) await setup.Database.MigrateAsync();
        var owner = Guid.NewGuid();
        var connection = await CreatePaidConnectionAsync(owner);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<BudgetReservationResult> Reserve(string key)
        {
            await using var context = CreateDbContext();
            var manager = new ProviderBudgetManager(context);
            await gate.Task;
            return await manager.ReserveAsync(BudgetRequest(owner, connection, key) with
            {
                MaxRequestsPerDay = 1,
                MaxRequestsPerMonth = 1,
            }, AiProviderKind.OpenAi, "gpt-5-mini", DateTimeOffset.UtcNow, CancellationToken.None);
        }
        var first = Reserve("concurrent:1");
        var second = Reserve("concurrent:2");
        gate.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.Allowed);
        Assert.Single(results, result => !result.Allowed);
    }

    [Fact]
    public async Task ConcurrentProductionExecutionsCannotExceedActualOutboundRequestCap()
    {
        await using (var setup = CreateDbContext()) await setup.Database.MigrateAsync();
        var owner = Guid.NewGuid();
        var connection = await CreatePaidConnectionAsync(owner);
        var provider = new BlockingCountingProvider();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<AiExecutionResult> Execute(string key)
        {
            await using var context = CreateDbContext();
            var service = new ProviderConnectionService(
                new ProviderConnectionRepository(context),
                new ProviderExecutionReceiptRepository(context),
                new ProviderBudgetManager(context),
                context,
                new PassingCredentialProtector(),
                [provider],
                new NeverToolExecutor(),
                new ProviderMetadataCanonicalizer(
                    ProviderCheckpointMetadataContract.CanonicalizeProviderRequestId,
                    ProviderCheckpointMetadataContract.CanonicalizeActualModel,
                    CheckpointEnvelopeBudget.MaximumCheckpointUtf8Bytes),
                TimeProvider.System);
            await start.Task;
            return await service.ExecuteAsync(BudgetRequest(owner, connection, key) with
            {
                MaxRequestsPerDay = 1,
                MaxRequestsPerMonth = 1,
            }, CancellationToken.None);
        }

        var first = Execute("production-concurrent:1");
        var second = Execute("production-concurrent:2");
        start.SetResult();
        await provider.FirstCallStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var rejected = await Task.WhenAny(first, second).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(rejected.IsCompletedSuccessfully);
        provider.ReleaseFirstCall.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.All(results, result => Assert.True(
            result.Succeeded || result.FailureKind == ProviderFailureKind.LocalBudgetExceeded));
        Assert.Equal(1, provider.CallCount);
        await using var verification = CreateDbContext();
        var receipt = Assert.Single(await verification.ProviderExecutionReceipts.AsNoTracking().ToArrayAsync());
        Assert.Equal(ProviderExecutionReceiptStatus.Released, receipt.Status);
        Assert.Equal(1, receipt.ReservedRequestCount);
        Assert.Equal(1, receipt.ActualRequestCount);
    }

    [Fact]
    public async Task PendingReservationAndWorstCaseCostBlockOverspendAndReconciliationReturnsRemainder()
    {
        await using (var setup = CreateDbContext()) await setup.Database.MigrateAsync();
        var owner = Guid.NewGuid();
        var connection = await CreatePaidConnectionAsync(owner);
        await using var db = CreateDbContext();
        var manager = new ProviderBudgetManager(db);
        var first = await manager.ReserveAsync(BudgetRequest(owner, connection, "budget:1") with
        {
            DailyCostCapUsd = 0.75m,
            MonthlyCostCapUsd = 0.75m,
        }, AiProviderKind.OpenAi, "gpt-5-mini", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(first.Allowed);
        var blockedByPending = await manager.ReserveAsync(BudgetRequest(owner, connection, "budget:2") with
        {
            DailyCostCapUsd = 0.75m,
            MonthlyCostCapUsd = 0.75m,
        }, AiProviderKind.OpenAi, "gpt-5-mini", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(blockedByPending.Allowed);

        await manager.ReconcileAsync(first.Receipt!, new AiExecutionResult(true, "ok", ProviderFailureKind.None,
            null, null, 10, 10, 0.1m, []), DateTimeOffset.UtcNow, CancellationToken.None);
        var afterReconciliation = await manager.ReserveAsync(BudgetRequest(owner, connection, "budget:3") with
        {
            DailyCostCapUsd = 0.75m,
            MonthlyCostCapUsd = 0.75m,
        }, AiProviderKind.OpenAi, "gpt-5-mini", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.True(afterReconciliation.Allowed);
    }

    [Fact]
    public async Task CompletedUsagePlusNextWorstCaseReservationCannotExceedCostCap()
    {
        await using (var setup = CreateDbContext()) await setup.Database.MigrateAsync();
        var owner = Guid.NewGuid();
        var connection = await CreatePaidConnectionAsync(owner);
        var now = DateTimeOffset.UtcNow;
        await using var db = CreateDbContext();
        var completed = ProviderExecutionReceipt.Start(owner, connection, "completed:0.90",
            now.AddMinutes(-1), AiProviderKind.OpenAi, 0.90m);
        completed.Complete("{\"succeeded\":true}", now.AddSeconds(-30), 0.90m);
        db.ProviderExecutionReceipts.Add(completed);
        await db.SaveChangesAsync();
        var manager = new ProviderBudgetManager(db);

        var blocked = await manager.ReserveAsync(BudgetRequest(owner, connection, "budget:next") with
        {
            DailyCostCapUsd = 1.00m,
            MonthlyCostCapUsd = 1.00m,
        }, AiProviderKind.OpenAi, "gpt-5-mini", now, CancellationToken.None);

        Assert.False(blocked.Allowed);
        Assert.Equal("local_budget_exceeded", blocked.ErrorCode);
        Assert.Equal(1, await db.ProviderExecutionReceipts.CountAsync());
    }

    [Fact]
    public async Task AmbiguousReservationRemainsReservedAndUnknownPriceFailsClosed()
    {
        await using (var setup = CreateDbContext()) await setup.Database.MigrateAsync();
        var owner = Guid.NewGuid();
        var connection = await CreatePaidConnectionAsync(owner);
        await using var db = CreateDbContext();
        var manager = new ProviderBudgetManager(db);
        var unknown = await manager.ReserveAsync(BudgetRequest(owner, connection, "unknown"),
            AiProviderKind.OpenAi, "future-unpriced-model", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(unknown.Allowed);
        Assert.Equal("paid_model_price_unknown", unknown.ErrorCode);

        var first = await manager.ReserveAsync(BudgetRequest(owner, connection, "ambiguous:1") with
        {
            MaxRequestsPerDay = 1,
        }, AiProviderKind.OpenAi, "gpt-5-mini", DateTimeOffset.UtcNow, CancellationToken.None);
        await manager.ReconcileAsync(first.Receipt!, new AiExecutionResult(false, null,
            ProviderFailureKind.AmbiguousTimeout, "timeout", null, 0, 0, null, []),
            DateTimeOffset.UtcNow, CancellationToken.None);
        var second = await manager.ReserveAsync(BudgetRequest(owner, connection, "ambiguous:2") with
        {
            MaxRequestsPerDay = 1,
        }, AiProviderKind.OpenAi, "gpt-5-mini", DateTimeOffset.UtcNow, CancellationToken.None);
        Assert.False(second.Allowed);
        Assert.Equal(ProviderExecutionReceiptStatus.Ambiguous,
            (await db.ProviderExecutionReceipts.SingleAsync(x => x.Id == first.Receipt!.Id)).Status);
    }

    private async Task<Guid> CreatePaidConnectionAsync(Guid owner)
    {
        await using var db = CreateDbContext();
        var connection = ProviderConnection.Create(owner, AiProviderKind.OpenAi, "Budget test",
            "protected", "sha256:test", DateTimeOffset.UtcNow);
        db.ProviderConnections.Add(connection);
        await db.SaveChangesAsync();
        return connection.Id;
    }

    private static AiExecutionRequest BudgetRequest(Guid owner, Guid connection, string key) => new(
        owner, connection, "gpt-5-mini", "system", "input", [], null, key,
        DailyCostCapUsd: 10, MonthlyCostCapUsd: 100,
        MaxRequestsPerDay: 100, MaxRequestsPerMonth: 1000,
        MaxInputTokens: 1_000_000, MaxOutputTokens: 125_000,
        RunCostCapUsd: 1, RouteCostCapUsd: 1,
        ExpectedProvider: AiProviderKind.OpenAi,
        MaxToolCycles: 0);

    private sealed class BlockingCountingProvider : IAiProvider
    {
        private int callCount;
        public AiProviderKind Kind => AiProviderKind.OpenAi;
        public int CallCount => Volatile.Read(ref callCount);
        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<bool> ValidateAsync(string? credential, CancellationToken token) => Task.FromResult(true);

        public async Task<IReadOnlyCollection<ProviderModelDto>> ListModelsAsync(
            string? credential,
            CancellationToken token)
        {
            Interlocked.Increment(ref callCount);
            FirstCallStarted.TrySetResult();
            await ReleaseFirstCall.Task.WaitAsync(token);
            return [new("gpt-5-mini", "gpt-5-mini", null, true, false, 0.25m, 2m)];
        }

        public Task<AiExecutionResult> ExecuteAsync(
            AiExecutionRequest request,
            string? credential,
            CancellationToken token)
        {
            Interlocked.Increment(ref callCount);
            return Task.FromResult(new AiExecutionResult(true, "ok", ProviderFailureKind.None,
                null, "request-1", 1, 1, 0.000001m, []));
        }
    }

    private sealed class PassingCredentialProtector : IProviderCredentialProtector
    {
        public string ProtectCredential(string credential, ProviderCredentialProtectionContext context) => credential;
        public string UnprotectCredential(string credential, ProviderCredentialProtectionContext context) => credential;
        public string ProtectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string UnprotectOAuthVerifier(string verifier, OAuthVerifierProtectionContext context) => verifier;
        public string Fingerprint(string credential) => "fingerprint";
    }

    private sealed class NeverToolExecutor : IToolExecutor
    {
        public Task<ToolInvocationResult> ExecuteAsync(
            ToolCallRequest request,
            string idempotencyKey,
            CancellationToken cancellationToken) => throw new Xunit.Sdk.XunitException(
                "Tool execution is not expected in the request-cap regression.");
    }

    private IntegrationsDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<IntegrationsDbContext>()
            .UseNpgsql(
                postgres.GetConnectionString(),
                options => options.MigrationsHistoryTable("__EFMigrationsHistory", "integrations"))
            .Options);

    private async Task AssertLatestSchemaAsync()
    {
        Assert.Equal(
            "integrations.provider_connections",
            await ScalarAsync<string?>("SELECT to_regclass('integrations.provider_connections')::text"));
        Assert.Equal(
            "integrations.provider_oauth_states",
            await ScalarAsync<string?>("SELECT to_regclass('integrations.provider_oauth_states')::text"));
        Assert.Equal(
            "integrations.provider_execution_receipts",
            await ScalarAsync<string?>("SELECT to_regclass('integrations.provider_execution_receipts')::text"));
        Assert.Equal(2, await ScalarAsync<long>("""
            SELECT count(*) FROM information_schema.columns
            WHERE table_schema = 'integrations'
              AND table_name = 'provider_execution_receipts'
              AND column_name IN ('reserved_request_count', 'actual_request_count')
            """));
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }
}
