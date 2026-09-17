using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861

namespace AiControlCenter.ControlPlane.Infrastructure.Persistence.Migrations;

/// <inheritdoc />
public partial class AddAgentDefinitions : Migration
{
    /// <inheritdoc />
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "agent_definitions",
            schema: "control_plane",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                direction_id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                execution_type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_agent_definitions", x => x.id);
                table.CheckConstraint("ck_agent_definitions_code_format", "char_length(code) BETWEEN 2 AND 64 AND code ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                table.CheckConstraint("ck_agent_definitions_execution_type", "execution_type = 'Test'");
                table.CheckConstraint("ck_agent_definitions_name_length", "char_length(btrim(name)) BETWEEN 2 AND 100");
                table.CheckConstraint("ck_agent_definitions_status", "status IN ('Inactive', 'Active')");
                table.CheckConstraint("ck_agent_definitions_timestamps", "updated_at >= created_at AND (archived_at IS NULL OR archived_at BETWEEN created_at AND updated_at)");
                table.ForeignKey(
                    name: "fk_agent_definitions_directions_direction_id",
                    column: x => x.direction_id,
                    principalSchema: "control_plane",
                    principalTable: "directions",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
            });

        migrationBuilder.CreateIndex(
            name: "IX_agent_definitions_direction_id",
            schema: "control_plane",
            table: "agent_definitions",
            column: "direction_id");

        migrationBuilder.CreateIndex(
            name: "ix_agent_definitions_list",
            schema: "control_plane",
            table: "agent_definitions",
            columns: new[] { "archived_at", "status", "direction_id", "name", "id" });

        migrationBuilder.CreateIndex(
            name: "ux_agent_definitions_code",
            schema: "control_plane",
            table: "agent_definitions",
            column: "code",
            unique: true);
    }

    /// <inheritdoc />
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(
            name: "agent_definitions",
            schema: "control_plane");
    }
}
