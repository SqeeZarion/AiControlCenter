using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiControlCenter.Orchestrator.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrchestratorDbContext))]
[Migration("20261104114500_PauseActiveRunSteps")]
public sealed class PauseActiveRunSteps : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_run_steps_lifecycle",
            schema: "orchestrator",
            table: "run_steps");
        migrationBuilder.DropCheckConstraint(
            name: "ck_run_steps_status",
            schema: "orchestrator",
            table: "run_steps");
        migrationBuilder.Sql("""
            UPDATE orchestrator.run_steps AS step
            SET status = 'Paused', updated_at = GREATEST(step.updated_at, run.started_at)
            FROM orchestrator.agent_runs AS run
            WHERE step.agent_run_id = run.id
              AND step.status = 'Running'
              AND run.status IN ('PausedProviderUnavailable', 'PausedInsufficientFunds');
            """);
        migrationBuilder.AddCheckConstraint(
            name: "ck_run_steps_lifecycle",
            schema: "orchestrator",
            table: "run_steps",
            sql: "(status IN ('Running','Paused') AND completed_at IS NULL) OR (status IN ('Succeeded','Failed') AND completed_at IS NOT NULL)");
        migrationBuilder.AddCheckConstraint(
            name: "ck_run_steps_status",
            schema: "orchestrator",
            table: "run_steps",
            sql: "status IN ('Running','Succeeded','Failed','Paused')");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_run_steps_lifecycle",
            schema: "orchestrator",
            table: "run_steps");
        migrationBuilder.DropCheckConstraint(
            name: "ck_run_steps_status",
            schema: "orchestrator",
            table: "run_steps");
        migrationBuilder.Sql("""
            UPDATE orchestrator.run_steps
            SET status = 'Running'
            WHERE status = 'Paused';
            """);
        migrationBuilder.AddCheckConstraint(
            name: "ck_run_steps_lifecycle",
            schema: "orchestrator",
            table: "run_steps",
            sql: "(status = 'Running' AND completed_at IS NULL) OR (status IN ('Succeeded','Failed') AND completed_at IS NOT NULL)");
        migrationBuilder.AddCheckConstraint(
            name: "ck_run_steps_status",
            schema: "orchestrator",
            table: "run_steps",
            sql: "status IN ('Running','Succeeded','Failed')");
    }
}
