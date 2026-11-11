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

        foreach (var pausedStatus in new[]
        {
            AgentRunStatus.PausedProviderUnavailable,
            AgentRunStatus.PausedInsufficientFunds,
        })
        {
            var paused = AgentRun.Create(
                Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), $"pause-{pausedStatus}",
                new AgentSnapshot(
                    "AI Agent", "ai-agent", null, 1, "Ai",
                    Guid.NewGuid(), "Direction", "direction", 1,
                    "System prompt", "[]", "[]", 100, 100, 1, 2),
                "input", TestRunOutcome.Succeed, now);
            var pausedMessageId = Guid.NewGuid();
            paused.Begin(pausedMessageId, now.AddSeconds(1));
            paused.Pause(pausedMessageId, pausedStatus, "Provider unavailable", now.AddSeconds(2));
            db.AgentRuns.Add(paused);
            await db.SaveChangesAsync();
            Assert.Equal(pausedStatus, paused.Status);
        }
    }

    [Fact]
    public void CurrentModelHasNoPendingChanges()
    {
        using var db = CreateDbContext();
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task LegacyAttemptsAreBackfilledBeforeStageFiveConstraintsAndUniqueIndex()
    {
        await using var db = CreateDbContext();
        var migrator = db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261006122341_AddAiRunAttemptsAndCheckpoints");
        var runId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var attemptId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync($"""
            INSERT INTO orchestrator.agent_runs
              (id, agent_id, owner_user_id, correlation_id, agent_name, agent_code, agent_version,
               direction_id, direction_name, direction_code, direction_version, execution_type, input,
               expected_outcome, status, revision, created_at, max_attempts)
            VALUES ('{runId}', '{Guid.NewGuid()}', '{ownerId}', 'legacy-correlation', 'Agent', 'agent', 1,
              '{Guid.NewGuid()}', 'Direction', 'direction', 1, 'Ai', 'input', 'Succeed', 'Queued', 1,
              '{now:O}', 3);
            INSERT INTO orchestrator.ai_provider_attempts
              (id, agent_run_id, sequence, provider, model, status, input_tokens, output_tokens,
               estimated_cost_usd, created_at)
            VALUES ('{attemptId}', '{runId}', 1, 'OpenAi', 'gpt-5-mini', 'Failed', 10, 2, 0.1, '{now:O}');
            """);

        await db.Database.MigrateAsync();

        Assert.Equal(ownerId, await ScalarAsync<Guid>(
            $"SELECT owner_user_id FROM orchestrator.ai_provider_attempts WHERE id='{attemptId}'"));
        Assert.Equal("legacy-correlation", await ScalarAsync<string>(
            $"SELECT correlation_id FROM orchestrator.ai_provider_attempts WHERE id='{attemptId}'"));
        Assert.Equal(1, await ScalarAsync<int>(
            $"SELECT request_count FROM orchestrator.ai_provider_attempts WHERE id='{attemptId}'"));
        Assert.Equal(1, await ScalarAsync<int>(
            $"SELECT route_priority FROM orchestrator.ai_provider_attempts WHERE id='{attemptId}'"));
        Assert.Equal(12, await ScalarAsync<int>(
            $"SELECT total_tokens FROM orchestrator.ai_provider_attempts WHERE id='{attemptId}'"));
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync($"""
            INSERT INTO orchestrator.ai_provider_attempts
              (id, agent_run_id, owner_user_id, sequence, provider, model, status, input_tokens,
               output_tokens, total_tokens, idempotency_key, route_priority, correlation_id,
               latency_milliseconds, request_count, usage_source, cost_source, finish_status,
               started_at, completed_at)
            SELECT '{Guid.NewGuid()}', agent_run_id, owner_user_id, 2, provider, model, status,
               input_tokens, output_tokens, total_tokens, idempotency_key, 2, correlation_id,
               0, 1, usage_source, cost_source, finish_status, started_at, completed_at
            FROM orchestrator.ai_provider_attempts WHERE id='{attemptId}';
            """));
    }

    [Fact]
    public async Task LegacyStageFourSchemaWithoutTerminalConstraintMigratesToLatest()
    {
        await using var db = CreateDbContext();
        var migrator = db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20260912151400_AddAgentRunsAndOutbox");
        await ExecuteAsync("""
            ALTER TABLE orchestrator.agent_runs
            DROP CONSTRAINT ck_agent_runs_terminal_payload;
            """);

        await db.Database.MigrateAsync();

        Assert.Equal(1, await ScalarAsync<int>("""
            SELECT count(*)::int
            FROM pg_constraint
            WHERE conrelid = 'orchestrator.agent_runs'::regclass
              AND conname = 'ck_agent_runs_terminal_payload'
            """));
        await AssertLatestSchemaAsync();
    }

    [Fact]
    public async Task StatusDowngradeRefusesLongPausedStatusBeforeAlteringColumnOrData()
    {
        await using var db = CreateDbContext();
        var migrator = db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261008121500_ExpandAgentRunStatusLength");
        var runId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync($"""
            INSERT INTO orchestrator.agent_runs
              (id, agent_id, owner_user_id, correlation_id, agent_name, agent_code, agent_version,
               direction_id, direction_name, direction_code, direction_version, execution_type, input,
               expected_outcome, status, revision, created_at, started_at, error, max_attempts)
            VALUES ('{runId}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', 'paused-correlation', 'Agent', 'agent', 1,
              '{Guid.NewGuid()}', 'Direction', 'direction', 1, 'Ai', 'input', 'Succeed',
              'PausedProviderUnavailable', 2, '{now.AddMinutes(-1):O}', '{now:O}', 'paused', 3);
            """);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            migrator.MigrateAsync("20261007194319_ExpandAiProviderAttemptAudit"));

        Assert.Equal(32, await ScalarAsync<int>("""
            SELECT character_maximum_length::int FROM information_schema.columns
            WHERE table_schema='orchestrator' AND table_name='agent_runs' AND column_name='status'
            """));
        Assert.Equal("PausedProviderUnavailable", await ScalarAsync<string>(
            $"SELECT status FROM orchestrator.agent_runs WHERE id='{runId}'"));
    }

    [Fact]
    public async Task DiagnosticsMigrationRepairsTerminalRunsWithLegacyRunningSteps()
    {
        await using var db = CreateDbContext();
        var migrator = db.Database.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>();
        await migrator.MigrateAsync("20261104114500_PauseActiveRunSteps");
        var runId = Guid.NewGuid();
        var stepId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await ExecuteAsync($"""
            INSERT INTO orchestrator.agent_runs
              (id, agent_id, owner_user_id, correlation_id, agent_name, agent_code, agent_version,
               direction_id, direction_name, direction_code, direction_version, execution_type, input,
               expected_outcome, status, revision, created_at, started_at, completed_at, error, max_attempts)
            VALUES ('{runId}', '{Guid.NewGuid()}', '{Guid.NewGuid()}', 'configuration-failure',
              'Agent', 'agent', 1, '{Guid.NewGuid()}', 'Direction', 'direction', 1, 'Ai', 'input',
              'Succeed', 'Failed', 5, '{now.AddMinutes(-1):O}', '{now.AddSeconds(-5):O}',
              '{now:O}', 'SessionCreate failed before generation dispatch.', 1);
            INSERT INTO orchestrator.run_steps
              (id, agent_run_id, sequence, name, status, log, started_at, updated_at, completed_at)
            VALUES ('{stepId}', '{runId}', 1, 'AI execution', 'Running', 'Provider execution started.',
              '{now.AddSeconds(-5):O}', '{now.AddSeconds(-5):O}', NULL);
            """);

        await db.Database.MigrateAsync();

        Assert.Equal("Failed", await ScalarAsync<string>(
            $"SELECT status FROM orchestrator.run_steps WHERE id='{stepId}'"));
        Assert.Equal("SessionCreate failed before generation dispatch.", await ScalarAsync<string>(
            $"SELECT log FROM orchestrator.run_steps WHERE id='{stepId}'"));
        Assert.True(await ScalarAsync<bool>(
            $"SELECT completed_at IS NOT NULL FROM orchestrator.run_steps WHERE id='{stepId}'"));
        Assert.Equal(4, await ScalarAsync<int>("""
            SELECT count(*)::int FROM information_schema.columns
            WHERE table_schema='orchestrator' AND table_name='ai_provider_attempts'
              AND column_name IN ('error_code','operation','generation_dispatched','safe_exception_type')
            """));
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
            "orchestrator.ai_provider_attempts",
            await ScalarAsync<string?>("SELECT to_regclass('orchestrator.ai_provider_attempts')::text"));
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

    private async Task<int> ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteNonQueryAsync();
    }
}
