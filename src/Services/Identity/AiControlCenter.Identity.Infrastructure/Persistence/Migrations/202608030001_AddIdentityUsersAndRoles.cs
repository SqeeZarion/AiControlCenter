using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiControlCenter.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("202608030001_AddIdentityUsersAndRoles")]
public sealed class AddIdentityUsersAndRoles : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.EnsureSchema("identity");

        migrationBuilder.CreateTable(
            name: "roles",
            schema: "identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                name = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                description = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
            },
            constraints: table => table.PrimaryKey("pk_roles", role => role.id));

        migrationBuilder.CreateTable(
            name: "users",
            schema: "identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                normalized_email = table.Column<string>(type: "character varying(320)", maxLength: 320, nullable: false),
                display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                password_hash = table.Column<string>(type: "character varying(1024)", maxLength: 1024, nullable: false),
                status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                must_change_password = table.Column<bool>(type: "boolean", nullable: false),
                access_failed_count = table.Column<int>(type: "integer", nullable: false),
                lockout_end = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                last_login_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            },
            constraints: table => table.PrimaryKey("pk_users", user => user.id));

        migrationBuilder.CreateTable(
            name: "user_roles",
            schema: "identity",
            columns: table => new
            {
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                role_id = table.Column<Guid>(type: "uuid", nullable: false),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_user_roles", userRole => new { userRole.user_id, userRole.role_id });
                table.ForeignKey(
                    name: "fk_user_roles_roles_role_id",
                    column: userRole => userRole.role_id,
                    principalSchema: "identity",
                    principalTable: "roles",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_user_roles_users_user_id",
                    column: userRole => userRole.user_id,
                    principalSchema: "identity",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.Sql(
            """
            INSERT INTO identity.roles (id, name, description)
            VALUES
                ('11111111-1111-4111-8111-111111111111', 'Admin', 'Full platform administration.'),
                ('22222222-2222-4222-8222-222222222222', 'Developer', 'Technical configuration and diagnostics.'),
                ('33333333-3333-4333-8333-333333333333', 'User', 'Standard authenticated platform access.');
            """);

        migrationBuilder.CreateIndex(
            name: "ux_roles_name",
            schema: "identity",
            table: "roles",
            column: "name",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ux_users_normalized_email",
            schema: "identity",
            table: "users",
            column: "normalized_email",
            unique: true);
        migrationBuilder.CreateIndex(
            name: "ix_user_roles_role_id",
            schema: "identity",
            table: "user_roles",
            column: "role_id");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable(name: "user_roles", schema: "identity");
        migrationBuilder.DropTable(name: "roles", schema: "identity");
        migrationBuilder.DropTable(name: "users", schema: "identity");
    }
}
