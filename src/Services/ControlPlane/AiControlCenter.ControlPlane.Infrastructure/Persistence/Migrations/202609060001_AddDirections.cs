using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiControlCenter.ControlPlane.Infrastructure.Persistence.Migrations;

[DbContext(typeof(ControlPlaneDbContext))]
[Migration("202609060001_AddDirections")]
public sealed class AddDirections : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("control_plane");
        migrationBuilder.CreateTable(
            name: "directions",
            schema: "control_plane",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                code = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                icon = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                sort_order = table.Column<int>(type: "integer", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                archived_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_directions", direction => direction.id);
                table.CheckConstraint(
                    "ck_directions_code_format",
                    "char_length(code) BETWEEN 2 AND 64 AND code ~ '^[a-z0-9]+(-[a-z0-9]+)*$'");
                table.CheckConstraint(
                    "ck_directions_name_length",
                    "char_length(btrim(name)) BETWEEN 2 AND 100");
                table.CheckConstraint("ck_directions_sort_order", "sort_order >= 0 AND sort_order <= 100000");
                table.CheckConstraint("ck_directions_status", "status IN ('Inactive', 'Active')");
                table.CheckConstraint(
                    "ck_directions_timestamps",
                    "updated_at >= created_at "
                    + "AND (archived_at IS NULL OR archived_at BETWEEN created_at AND updated_at)");
            });

        migrationBuilder.CreateIndex(
            name: "ix_directions_list",
            schema: "control_plane",
            table: "directions",
            columns: ["archived_at", "status", "sort_order", "name", "id"]);
        migrationBuilder.CreateIndex(
            name: "ux_directions_code",
            schema: "control_plane",
            table: "directions",
            column: "code",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "directions", schema: "control_plane");
}
