using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable IDE0161

namespace AiControlCenter.ControlPlane.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiAgentConfiguration : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_definitions_execution_type",
                schema: "control_plane",
                table: "agent_definitions");

            migrationBuilder.AddColumn<int>(
                name: "max_attempts",
                schema: "control_plane",
                table: "agent_definitions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<decimal>(
                name: "max_estimated_cost_usd",
                schema: "control_plane",
                table: "agent_definitions",
                type: "numeric(18,8)",
                precision: 18,
                scale: 8,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_input_tokens",
                schema: "control_plane",
                table: "agent_definitions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "max_output_tokens",
                schema: "control_plane",
                table: "agent_definitions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "model_routes_json",
                schema: "control_plane",
                table: "agent_definitions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "system_prompt",
                schema: "control_plane",
                table: "agent_definitions",
                type: "character varying(16000)",
                maxLength: 16000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "tool_policy_json",
                schema: "control_plane",
                table: "agent_definitions",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_definitions_ai_configuration",
                schema: "control_plane",
                table: "agent_definitions",
                sql: "(execution_type = 'Test' AND system_prompt IS NULL AND model_routes_json IS NULL) OR (execution_type = 'Ai' AND system_prompt IS NOT NULL AND model_routes_json IS NOT NULL AND max_input_tokens > 0 AND max_output_tokens > 0 AND max_estimated_cost_usd > 0 AND max_attempts BETWEEN 1 AND 10)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_definitions_execution_type",
                schema: "control_plane",
                table: "agent_definitions",
                sql: "execution_type IN ('Test','Ai')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_definitions_ai_configuration",
                schema: "control_plane",
                table: "agent_definitions");

            migrationBuilder.DropCheckConstraint(
                name: "ck_agent_definitions_execution_type",
                schema: "control_plane",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "max_attempts",
                schema: "control_plane",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "max_estimated_cost_usd",
                schema: "control_plane",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "max_input_tokens",
                schema: "control_plane",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "max_output_tokens",
                schema: "control_plane",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "model_routes_json",
                schema: "control_plane",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "system_prompt",
                schema: "control_plane",
                table: "agent_definitions");

            migrationBuilder.DropColumn(
                name: "tool_policy_json",
                schema: "control_plane",
                table: "agent_definitions");

            migrationBuilder.AddCheckConstraint(
                name: "ck_agent_definitions_execution_type",
                schema: "control_plane",
                table: "agent_definitions",
                sql: "execution_type = 'Test'");
        }
    }
}
