using AiControlCenter.Integrations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiControlCenter.Integrations.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IntegrationsDbContext))]
[Migration("20261012120000_AddProviderRequestAccounting")]
public sealed class AddProviderRequestAccounting : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<int>(
            name: "reserved_request_count",
            schema: "integrations",
            table: "provider_execution_receipts",
            type: "integer",
            nullable: false,
            defaultValue: 1);
        migrationBuilder.AddColumn<int>(
            name: "actual_request_count",
            schema: "integrations",
            table: "provider_execution_receipts",
            type: "integer",
            nullable: false,
            defaultValue: 0);
        migrationBuilder.Sql("""
            UPDATE integrations.provider_execution_receipts
            SET actual_request_count = CASE WHEN status = 'Pending' THEN 0 ELSE 1 END;
            """);
        migrationBuilder.AddCheckConstraint(
            name: "ck_provider_execution_receipts_requests",
            schema: "integrations",
            table: "provider_execution_receipts",
            sql: "reserved_request_count > 0 AND actual_request_count >= 0 "
                + "AND actual_request_count <= reserved_request_count");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(
            name: "ck_provider_execution_receipts_requests",
            schema: "integrations",
            table: "provider_execution_receipts");
        migrationBuilder.DropColumn(
            name: "actual_request_count",
            schema: "integrations",
            table: "provider_execution_receipts");
        migrationBuilder.DropColumn(
            name: "reserved_request_count",
            schema: "integrations",
            table: "provider_execution_receipts");
    }
}
