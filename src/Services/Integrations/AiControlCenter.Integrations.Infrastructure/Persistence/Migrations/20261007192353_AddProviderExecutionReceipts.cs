using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
#pragma warning disable CA1861, IDE0161

namespace AiControlCenter.Integrations.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddProviderExecutionReceipts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "provider_execution_receipts",
                schema: "integrations",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    owner_user_id = table.Column<Guid>(type: "uuid", nullable: false),
                    connection_id = table.Column<Guid>(type: "uuid", nullable: false),
                    idempotency_key = table.Column<string>(type: "character varying(160)", maxLength: 160, nullable: false),
                    status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    result_json = table.Column<string>(type: "character varying(65536)", maxLength: 65536, nullable: true),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    completed_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    xmin = table.Column<uint>(type: "xid", rowVersion: true, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_provider_execution_receipts", x => x.id);
                    table.CheckConstraint("ck_provider_execution_receipts_lifecycle", "(status = 'Pending' AND result_json IS NULL AND completed_at IS NULL) OR (status = 'Completed' AND result_json IS NOT NULL AND completed_at >= created_at)");
                    table.CheckConstraint("ck_provider_execution_receipts_status", "status IN ('Pending','Completed')");
                });

            migrationBuilder.CreateIndex(
                name: "ix_provider_execution_receipts_connection_created",
                schema: "integrations",
                table: "provider_execution_receipts",
                columns: new[] { "connection_id", "created_at" });

            migrationBuilder.CreateIndex(
                name: "ux_provider_execution_receipts_owner_idempotency",
                schema: "integrations",
                table: "provider_execution_receipts",
                columns: new[] { "owner_user_id", "idempotency_key" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "provider_execution_receipts",
                schema: "integrations");
        }
    }
}
