using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861, IDE0161

namespace AiControlCenter.Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExpandAiProviderAttemptAudit : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "created_at",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                newName: "started_at");

            migrationBuilder.AddColumn<string>(
                name: "actual_model",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "cached_input_tokens",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "completed_at",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "correlation_id",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "cost_source",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "fallback_reason",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "finish_status",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "idempotency_key",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "character varying(160)",
                maxLength: 160,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "latency_milliseconds",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "owner_user_id",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "partial_output",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "character varying(4000)",
                maxLength: 4000,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "request_count",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "route_priority",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "total_tokens",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "usage_source",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                type: "character varying(24)",
                maxLength: 24,
                nullable: true);

            migrationBuilder.Sql("""
                UPDATE orchestrator.ai_provider_attempts AS attempt
                SET owner_user_id = run.owner_user_id,
                    completed_at = attempt.started_at,
                    correlation_id = run.correlation_id,
                    usage_source = 'Unavailable',
                    cost_source = 'Unavailable',
                    finish_status = CASE WHEN attempt.status = 'Succeeded' THEN 'Completed' ELSE 'Unknown' END,
                    idempotency_key = 'legacy:' || attempt.agent_run_id::text || ':' || attempt.sequence::text,
                    latency_milliseconds = 0,
                    request_count = 1,
                    route_priority = GREATEST(1, attempt.sequence),
                    total_tokens = attempt.input_tokens + attempt.output_tokens
                FROM orchestrator.agent_runs AS run
                WHERE run.id = attempt.agent_run_id;

                DO $$
                BEGIN
                    IF EXISTS (
                        SELECT 1 FROM orchestrator.ai_provider_attempts
                        WHERE owner_user_id IS NULL OR owner_user_id = '00000000-0000-0000-0000-000000000000'
                           OR completed_at IS NULL OR completed_at < started_at
                           OR correlation_id IS NULL OR btrim(correlation_id) = ''
                           OR idempotency_key IS NULL OR btrim(idempotency_key) = ''
                           OR request_count <> 1 OR route_priority <= 0
                           OR total_tokens <> input_tokens + output_tokens)
                    THEN RAISE EXCEPTION 'Legacy provider attempt audit backfill validation failed.';
                    END IF;
                END $$;
                """);

            migrationBuilder.AlterColumn<DateTimeOffset>("completed_at", "ai_provider_attempts",
                type: "timestamp with time zone", schema: "orchestrator", nullable: false,
                oldClrType: typeof(DateTimeOffset), oldType: "timestamp with time zone", oldNullable: true);
            migrationBuilder.AlterColumn<string>("correlation_id", "ai_provider_attempts",
                type: "character varying(128)", schema: "orchestrator", maxLength: 128, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(128)", oldMaxLength: 128, oldNullable: true);
            migrationBuilder.AlterColumn<string>("cost_source", "ai_provider_attempts",
                type: "character varying(24)", schema: "orchestrator", maxLength: 24, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(24)", oldMaxLength: 24, oldNullable: true);
            migrationBuilder.AlterColumn<string>("finish_status", "ai_provider_attempts",
                type: "character varying(32)", schema: "orchestrator", maxLength: 32, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(32)", oldMaxLength: 32, oldNullable: true);
            migrationBuilder.AlterColumn<string>("idempotency_key", "ai_provider_attempts",
                type: "character varying(160)", schema: "orchestrator", maxLength: 160, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(160)", oldMaxLength: 160, oldNullable: true);
            migrationBuilder.AlterColumn<long>("latency_milliseconds", "ai_provider_attempts",
                type: "bigint", schema: "orchestrator", nullable: false,
                oldClrType: typeof(long), oldType: "bigint", oldNullable: true);
            migrationBuilder.AlterColumn<Guid>("owner_user_id", "ai_provider_attempts",
                type: "uuid", schema: "orchestrator", nullable: false,
                oldClrType: typeof(Guid), oldType: "uuid", oldNullable: true);
            migrationBuilder.AlterColumn<int>("request_count", "ai_provider_attempts",
                type: "integer", schema: "orchestrator", nullable: false,
                oldClrType: typeof(int), oldType: "integer", oldNullable: true);
            migrationBuilder.AlterColumn<int>("route_priority", "ai_provider_attempts",
                type: "integer", schema: "orchestrator", nullable: false,
                oldClrType: typeof(int), oldType: "integer", oldNullable: true);
            migrationBuilder.AlterColumn<int>("total_tokens", "ai_provider_attempts",
                type: "integer", schema: "orchestrator", nullable: false,
                oldClrType: typeof(int), oldType: "integer", oldNullable: true);
            migrationBuilder.AlterColumn<string>("usage_source", "ai_provider_attempts",
                type: "character varying(24)", schema: "orchestrator", maxLength: 24, nullable: false,
                oldClrType: typeof(string), oldType: "character varying(24)", oldMaxLength: 24, oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "ux_ai_provider_attempts_owner_idempotency",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                columns: new[] { "owner_user_id", "idempotency_key" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_provider_attempts_route",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                sql: "route_priority > 0 AND latency_milliseconds >= 0 AND request_count = 1");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_provider_attempts_timestamps",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                sql: "completed_at >= started_at");

            migrationBuilder.AddCheckConstraint(
                name: "ck_ai_provider_attempts_totals",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                sql: "(cached_input_tokens IS NULL OR cached_input_tokens >= 0) AND total_tokens = input_tokens + output_tokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "ux_ai_provider_attempts_owner_idempotency",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_provider_attempts_route",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_provider_attempts_timestamps",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropCheckConstraint(
                name: "ck_ai_provider_attempts_totals",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "actual_model",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "cached_input_tokens",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "completed_at",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "correlation_id",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "cost_source",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "fallback_reason",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "finish_status",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "idempotency_key",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "latency_milliseconds",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "owner_user_id",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "partial_output",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "request_count",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "route_priority",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "total_tokens",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.DropColumn(
                name: "usage_source",
                schema: "orchestrator",
                table: "ai_provider_attempts");

            migrationBuilder.RenameColumn(
                name: "started_at",
                schema: "orchestrator",
                table: "ai_provider_attempts",
                newName: "created_at");
        }
    }
}
