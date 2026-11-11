using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiControlCenter.Orchestrator.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrchestratorDbContext))]
[Migration("20261109123500_PersistAiProviderAttemptDiagnostics")]
public sealed class PersistAiProviderAttemptDiagnostics : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(
            name: "error_code",
            schema: "orchestrator",
            table: "ai_provider_attempts",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "generation_dispatched",
            schema: "orchestrator",
            table: "ai_provider_attempts",
            type: "character varying(16)",
            maxLength: 16,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "operation",
            schema: "orchestrator",
            table: "ai_provider_attempts",
            type: "character varying(64)",
            maxLength: 64,
            nullable: true);

        migrationBuilder.AddColumn<string>(
            name: "safe_exception_type",
            schema: "orchestrator",
            table: "ai_provider_attempts",
            type: "character varying(128)",
            maxLength: 128,
            nullable: true);

        migrationBuilder.AddCheckConstraint(
            name: "ck_ai_provider_attempts_generation_dispatched",
            schema: "orchestrator",
            table: "ai_provider_attempts",
            sql: "generation_dispatched IS NULL OR generation_dispatched IN ('true','false','unknown')");

        migrationBuilder.Sql(
            """
            UPDATE orchestrator.run_steps AS step
            SET status = 'Failed',
                log = run.error,
                updated_at = GREATEST(step.updated_at, step.started_at, run.completed_at),
                completed_at = GREATEST(step.updated_at, step.started_at, run.completed_at)
            FROM orchestrator.agent_runs AS run
            WHERE step.agent_run_id = run.id
              AND run.status = 'Failed'
              AND run.completed_at IS NOT NULL
              AND run.error IS NOT NULL
              AND step.status = 'Running';
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_ai_provider_attempts_generation_dispatched",
            schema: "orchestrator",
            table: "ai_provider_attempts");

        migrationBuilder.DropColumn(
            name: "error_code",
            schema: "orchestrator",
            table: "ai_provider_attempts");

        migrationBuilder.DropColumn(
            name: "generation_dispatched",
            schema: "orchestrator",
            table: "ai_provider_attempts");

        migrationBuilder.DropColumn(
            name: "operation",
            schema: "orchestrator",
            table: "ai_provider_attempts");

        migrationBuilder.DropColumn(
            name: "safe_exception_type",
            schema: "orchestrator",
            table: "ai_provider_attempts");
    }
}
