using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861, IDE0161

namespace AiControlCenter.Orchestrator.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ExpandAgentRunStatusLength : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "status",
                schema: "orchestrator",
                table: "agent_runs",
                type: "character varying(32)",
                maxLength: 32,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$
                BEGIN
                    IF EXISTS (SELECT 1 FROM orchestrator.agent_runs WHERE length(status) > 16) THEN
                        RAISE EXCEPTION 'Cannot downgrade agent run status: values longer than 16 characters exist.';
                    END IF;
                END $$;
                """);
            migrationBuilder.AlterColumn<string>(
                name: "status",
                schema: "orchestrator",
                table: "agent_runs",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(32)",
                oldMaxLength: 32);
        }
    }
}
