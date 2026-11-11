using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiControlCenter.Orchestrator.Infrastructure.Persistence.Migrations;

[DbContext(typeof(OrchestratorDbContext))]
[Migration("20261010123000_AddPersistedAiRunCheckpoint")]
public sealed class AddPersistedAiRunCheckpoint : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "checkpoint_json", table: "agent_runs", type: "jsonb", schema: "orchestrator", nullable: true);
        migrationBuilder.AddColumn<int>(name: "checkpoint_version", table: "agent_runs", type: "integer", schema: "orchestrator", nullable: false, defaultValue: 0);
        migrationBuilder.AddColumn<string>(name: "checkpoint_hash", table: "agent_runs", type: "character varying(64)", schema: "orchestrator", maxLength: 64, nullable: true);
        migrationBuilder.AddColumn<DateTimeOffset>(name: "checkpoint_updated_at", table: "agent_runs", type: "timestamp with time zone", schema: "orchestrator", nullable: true);
        migrationBuilder.AddCheckConstraint("ck_agent_runs_checkpoint", "agent_runs", """
            (checkpoint_json IS NULL AND checkpoint_version = 0 AND checkpoint_hash IS NULL AND checkpoint_updated_at IS NULL)
            OR
            (checkpoint_json IS NOT NULL AND checkpoint_version > 0 AND length(checkpoint_hash) = 64 AND checkpoint_updated_at IS NOT NULL)
            """, "orchestrator");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint("ck_agent_runs_checkpoint", "agent_runs", "orchestrator");
        migrationBuilder.DropColumn("checkpoint_json", "agent_runs", "orchestrator");
        migrationBuilder.DropColumn("checkpoint_version", "agent_runs", "orchestrator");
        migrationBuilder.DropColumn("checkpoint_hash", "agent_runs", "orchestrator");
        migrationBuilder.DropColumn("checkpoint_updated_at", "agent_runs", "orchestrator");
    }
}
