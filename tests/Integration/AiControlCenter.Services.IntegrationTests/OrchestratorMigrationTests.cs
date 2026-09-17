using AiControlCenter.Orchestrator.Domain;
using AiControlCenter.Orchestrator.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Npgsql;
using Testcontainers.PostgreSql;

namespace AiControlCenter.Services.IntegrationTests;

public sealed class OrchestratorMigrationTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer postgres = new PostgreSqlBuilder("postgres:17.5-alpine")
        .WithDatabase("orchestrator_migration_tests")
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
        Assert.Null(await ScalarAsync<string?>("SELECT to_regclass('orchestrator.agent_runs')::text"));

        await db.Database.MigrateAsync();
        await AssertLatestSchemaAsync();
    }

    [Fact]
    public async Task DatabaseConstraintsMatchRowLocalDomainInvariants()
    {
        await using var db = CreateDbContext();
        await db.Database.MigrateAsync();
        var now = DateTimeOffset.UtcNow;
        var run = AgentRun.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "constraint-test",
            new AgentSnapshot(
                "Agent", "agent", null, 1, "Test",
                Guid.NewGuid(), "Direction", "direction", 1),
            string.Empty, TestRunOutcome.Succeed, now);
        db.AgentRuns.Add(run);
        await db.SaveChangesAsync();

        await AssertCheckViolationAsync(
            $"UPDATE orchestrator.agent_runs SET agent_version=0 WHERE id='{run.Id}'");
        await AssertCheckViolationAsync(
            $"UPDATE orchestrator.agent_runs SET result='premature' WHERE id='{run.Id}'");

        var failed = AgentRun.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "failed-test",
            new AgentSnapshot(
                "Agent", "agent", null, 1, "Test",
                Guid.NewGuid(), "Direction", "direction", 1),
            string.Empty, TestRunOutcome.Fail, now);
        failed.FailBeforeOrDuringStart(Guid.NewGuid(), "controlled", now);
        db.AgentRuns.Add(failed);
        await db.SaveChangesAsync();
        await AssertCheckViolationAsync(
            $"UPDATE orchestrator.agent_runs SET error='' WHERE id='{failed.Id}'");

        var succeeded = AgentRun.Create(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "success-test",
            new AgentSnapshot(
                "Agent", "agent", null, 1, "Test",
                Guid.NewGuid(), "Direction", "direction", 1),
            string.Empty, TestRunOutcome.Succeed, now);
        var messageId = Guid.NewGuid();
        succeeded.Begin(messageId, now);
        succeeded.ReportStep(messageId, 1, "Step", RunStepStatus.Running, null, now);
        succeeded.ReportStep(messageId, 1, "Step", RunStepStatus.Succeeded, null, now);
        succeeded.Complete(messageId, AgentRunStatus.Succeeded, null, null, now);
        db.AgentRuns.Add(succeeded);
        await db.SaveChangesAsync();
    }

    private OrchestratorDbContext CreateDbContext() => new(
        new DbContextOptionsBuilder<OrchestratorDbContext>()
            .UseNpgsql(
                postgres.GetConnectionString(),
                options => options.MigrationsHistoryTable("__EFMigrationsHistory", "orchestrator"))
            .Options);

    private async Task AssertLatestSchemaAsync()
    {
        Assert.Equal(
            "orchestrator.agent_runs",
            await ScalarAsync<string?>("SELECT to_regclass('orchestrator.agent_runs')::text"));
        Assert.Equal(
            "orchestrator.run_steps",
            await ScalarAsync<string?>("SELECT to_regclass('orchestrator.run_steps')::text"));
        Assert.Equal(
            "orchestrator.\"OutboxMessage\"",
            await ScalarAsync<string?>("SELECT to_regclass('orchestrator.\"OutboxMessage\"')::text"));
    }

    private async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    private async Task AssertCheckViolationAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var exception = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
    }
}
