using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace AiControlCenter.Identity.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IdentityDbContext))]
[Migration("202608030002_AddRefreshTokenSessions")]
public sealed class AddRefreshTokenSessions : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "refresh_tokens",
            schema: "identity",
            columns: table => new
            {
                id = table.Column<Guid>(type: "uuid", nullable: false),
                user_id = table.Column<Guid>(type: "uuid", nullable: false),
                token_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                family_id = table.Column<Guid>(type: "uuid", nullable: false),
                created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                revocation_reason = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                replaced_by_token_id = table.Column<Guid>(type: "uuid", nullable: true),
            },
            constraints: table =>
            {
                table.PrimaryKey("pk_refresh_tokens", token => token.id);
                table.ForeignKey(
                    name: "fk_refresh_tokens_refresh_tokens_replaced_by_token_id",
                    column: token => token.replaced_by_token_id,
                    principalSchema: "identity",
                    principalTable: "refresh_tokens",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Restrict);
                table.ForeignKey(
                    name: "fk_refresh_tokens_users_user_id",
                    column: token => token.user_id,
                    principalSchema: "identity",
                    principalTable: "users",
                    principalColumn: "id",
                    onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex(
            name: "ix_refresh_tokens_family_id",
            schema: "identity",
            table: "refresh_tokens",
            column: "family_id");
        migrationBuilder.CreateIndex(
            name: "ix_refresh_tokens_replaced_by_token_id",
            schema: "identity",
            table: "refresh_tokens",
            column: "replaced_by_token_id");
        migrationBuilder.CreateIndex(
            name: "ix_refresh_tokens_user_expiration",
            schema: "identity",
            table: "refresh_tokens",
            columns: ["user_id", "expires_at"]);
        migrationBuilder.CreateIndex(
            name: "ux_refresh_tokens_hash",
            schema: "identity",
            table: "refresh_tokens",
            column: "token_hash",
            unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropTable(name: "refresh_tokens", schema: "identity");
}
