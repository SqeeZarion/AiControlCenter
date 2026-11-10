using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861, IDE0161

namespace AiControlCenter.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddAiProviderConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.EnsureSchema(
                name: "integrations");

            migrationBuilder.CreateTable(
                name: "provider_connections",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    provider = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    display_name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    protected_credential = table.Column<string>(type: "character varying(16384)", maxLength: 16384, nullable: true),
                    credential_fingerprint = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    last_validated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    revoked_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_connections", x => x.id);
                    table.CheckConstraint("ck_provider_connections_provider", "provider IN ('GitHubCopilot','OpenAi','XAi','DeterministicTest')");
                    table.CheckConstraint("ck_provider_connections_secret", "(provider = 'DeterministicTest') OR (status = 'Revoked') OR protected_credential IS NOT NULL");
                    table.CheckConstraint("ck_provider_connections_status", "status IN ('Active','Invalid','Revoked')");
                    table.CheckConstraint("ck_provider_connections_timestamps", "updated_at >= created_at AND (revoked_at IS NULL OR revoked_at >= created_at)");
                });

            migrationBuilder.CreateTable(
                name: "provider_oauth_states",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    state_hash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    code_verifier_protected = table.Column<string>(type: "character varying(4096)", maxLength: 4096, nullable: false),
                    redirect_uri = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    expires_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    consumed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_oauth_states", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "ux_provider_connections_owner_provider_name",
                schema: "integrations",
                table: "provider_connections",
                columns: new[] { "owner_user_id", "provider", "display_name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "ux_provider_oauth_states_hash",
                schema: "integrations",
                table: "provider_oauth_states",
                column: "state_hash",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_connections",
                schema: "integrations");

            migrationBuilder.DropTable(
                name: "provider_oauth_states",
                schema: "integrations");
        }
    }
}
