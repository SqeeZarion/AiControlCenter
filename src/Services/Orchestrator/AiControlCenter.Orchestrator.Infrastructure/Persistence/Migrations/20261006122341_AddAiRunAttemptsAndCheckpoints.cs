using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861, IDE0161

namespace AiControlCenter.Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiRunAttemptsAndCheckpoints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                ALTER TABLE orchestrator.agent_runs
                    DROP CONSTRAINT IF EXISTS ck_agent_runs_execution_type,
                    DROP CONSTRAINT IF EXISTS ck_agent_runs_lifecycle,
                    DROP CONSTRAINT IF EXISTS ck_agent_runs_status,
                    DROP CONSTRAINT IF EXISTS ck_agent_runs_terminal_payload;
                """);

            migrationBuilder.AddColumn<int>(
                name: "max_attempts",
                schema: "orchestrator",
                table: "agent_runs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "max_estimated_cost_usd",
                schema: "orchestrator",
                table: "agent_runs",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_input_tokens",
                schema: "orchestrator",
                table: "agent_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_output_tokens",
                schema: "orchestrator",
                table: "agent_runs",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_routes_json",
                schema: "orchestrator",
                table: "agent_runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "system_prompt",
                schema: "orchestrator",
                table: "agent_runs",
                type: "character varying(16000)",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tool_policy_json",
                schema: "orchestrator",
                table: "agent_runs",
                type: "jsonb",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "ai_provider_attempts",
                schema: "orchestrator",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    agent_run_id = table.Column<Guid>(type: "uuid", nullable: false),
                    sequence = table.Column<int>(type: "integer", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    model = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    failure_kind = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    provider_request_id = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    input_tokens = table.Column<int>(type: "integer", nullable: false),
                    output_tokens = table.Column<int>(type: "integer", nullable: false),
                    estimated_cost_usd = table.Column<decimal>(type: "numeric(18,8)", precision: 18, scale: 8, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ai_provider_attempts", x => x.id);
                    table.CheckConstraint("ck_ai_provider_attempts_cost", "estimated_cost_usd IS NULL OR estimated_cost_usd >= 0");
                    table.CheckConstraint("ck_ai_provider_attempts_sequence", "sequence > 0");
                    table.CheckConstraint("ck_ai_provider_attempts_tokens", "input_tokens >= 0 AND output_tokens >= 0");
                    table.ForeignKey(
                        name: "fk_ai_provider_attempts_agent_runs_run_id",
                        column: x => x.agent_run_id,
                        principalSchema: "orchestrator",
                        principalTable: "agent_runs",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_runs_execution_type",
                schema: "orchestrator",
                table: "agent_runs",
                sql: "execution_type IN ('Test','Ai')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_runs_lifecycle",
                schema: "orchestrator",
                table: "agent_runs",
                sql: "(status = 'Queued' AND started_at IS NULL AND completed_at IS NULL) OR (status = 'Running' AND started_at IS NOT NULL AND completed_at IS NULL) OR (status = 'Succeeded' AND started_at IS NOT NULL AND completed_at IS NOT NULL) OR (status = 'Failed' AND completed_at IS NOT NULL) OR (status IN ('PausedProviderUnavailable','PausedInsufficientFunds') AND started_at IS NOT NULL AND completed_at IS NULL) OR (status = 'Cancelled' AND completed_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_runs_status",
                schema: "orchestrator",
                table: "agent_runs",
                sql: "status IN ('Queued','Running','Succeeded','Failed','PausedProviderUnavailable','PausedInsufficientFunds','Cancelled')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_runs_terminal_payload",
                schema: "orchestrator",
                table: "agent_runs",
                sql: "(status IN ('Queued','Running') AND result IS NULL AND error IS NULL) OR (status IN ('PausedProviderUnavailable','PausedInsufficientFunds') AND result IS NULL AND error IS NOT NULL) OR (status = 'Succeeded' AND error IS NULL) OR (status IN ('Failed','Cancelled') AND error IS NOT NULL AND btrim(error) <> '')");

            migrationBuilder.CreateIndex(
                name: "ux_ai_provider_attempts_run_sequence",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                columns: new[] { "agent_run_id", "sequence" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ai_provider_attempts",
                schema: "orchestrator");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_runs_execution_type",
                schema: "orchestrator",
                table: "agent_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_runs_lifecycle",
                schema: "orchestrator",
                table: "agent_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_runs_status",
                schema: "orchestrator",
                table: "agent_runs");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_runs_terminal_payload",
                schema: "orchestrator",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "max_attempts",
                schema: "orchestrator",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "max_estimated_cost_usd",
                schema: "orchestrator",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "max_input_tokens",
                schema: "orchestrator",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "max_output_tokens",
                schema: "orchestrator",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "model_routes_json",
                schema: "orchestrator",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "system_prompt",
                schema: "orchestrator",
                table: "agent_runs");

            migrationBuilder.DropColumn(
                name: "tool_policy_json",
                schema: "orchestrator",
                table: "agent_runs");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_runs_execution_type",
                schema: "orchestrator",
                table: "agent_runs",
                sql: "execution_type = 'Test'");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_runs_lifecycle",
                schema: "orchestrator",
                table: "agent_runs",
                sql: "(status = 'Queued' AND started_at IS NULL AND completed_at IS NULL) OR (status = 'Running' AND started_at IS NOT NULL AND completed_at IS NULL) OR (status = 'Succeeded' AND started_at IS NOT NULL AND completed_at IS NOT NULL) OR (status = 'Failed' AND completed_at IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_runs_status",
                schema: "orchestrator",
                table: "agent_runs",
                sql: "status IN ('Queued','Running','Succeeded','Failed')");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_runs_terminal_payload",
                schema: "orchestrator",
                table: "agent_runs",
                sql: "(status IN ('Queued','Running') AND result IS NULL AND error IS NULL) OR (status = 'Succeeded' AND error IS NULL) OR (status = 'Failed' AND error IS NOT NULL AND btrim(error) <> '')");
        }
    }
}
