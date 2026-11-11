using AiControlCenter.Integrations.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AiControlCenter.Integrations.Infrastructure.Persistence.Migrations;

[DbContext(typeof(IntegrationsDbContext))]
[Migration("20261010120000_AddAtomicProviderBudgetReservations")]
public sealed class AddAtomicProviderBudgetReservations : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.AddColumn<string>(name: "provider", schema: "integrations",
            table: "provider_execution_receipts", type: "character varying(32)", maxLength: 32, nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "reserved_cost_usd", schema: "integrations",
            table: "provider_execution_receipts", type: "numeric(18,8)", precision: 18, scale: 8, nullable: true);
        migrationBuilder.AddColumn<decimal>(name: "actual_cost_usd", schema: "integrations",
            table: "provider_execution_receipts", type: "numeric(18,8)", precision: 18, scale: 8, nullable: true);
        migrationBuilder.Sql("""
            UPDATE integrations.provider_execution_receipts AS receipt
            SET provider = connection.provider,
                reserved_cost_usd = GREATEST(COALESCE(
                    CASE WHEN receipt.result_json IS NOT NULL
                         THEN (receipt.result_json::jsonb ->> 'EstimatedCostUsd')::numeric
                         ELSE 0 END, 0), 0),
                actual_cost_usd = CASE WHEN receipt.status = 'Completed' THEN GREATEST(COALESCE(
                    (receipt.result_json::jsonb ->> 'EstimatedCostUsd')::numeric, 0), 0) ELSE NULL END
            FROM integrations.provider_connections AS connection
            WHERE connection.id = receipt.connection_id;

            DO $$
            BEGIN
                IF EXISTS (
                    SELECT 1 FROM integrations.provider_execution_receipts
                    WHERE provider IS NULL OR reserved_cost_usd IS NULL OR reserved_cost_usd < 0
                       OR (actual_cost_usd IS NOT NULL AND actual_cost_usd < 0)) THEN
                    RAISE EXCEPTION 'Provider execution receipt budget backfill failed validation.';
                END IF;
            END $$;
            """);
        migrationBuilder.AlterColumn<string>(name: "provider", schema: "integrations",
            table: "provider_execution_receipts", type: "character varying(32)", maxLength: 32, nullable: false,
            oldClrType: typeof(string), oldType: "character varying(32)", oldMaxLength: 32, oldNullable: true);
        migrationBuilder.AlterColumn<decimal>(name: "reserved_cost_usd", schema: "integrations",
            table: "provider_execution_receipts", type: "numeric(18,8)", precision: 18, scale: 8, nullable: false,
            oldClrType: typeof(decimal), oldType: "numeric(18,8)", oldPrecision: 18, oldScale: 8, oldNullable: true);
        migrationBuilder.DropCheckConstraint(name: "ck_provider_execution_receipts_lifecycle",
            schema: "integrations", table: "provider_execution_receipts");
        migrationBuilder.DropCheckConstraint(name: "ck_provider_execution_receipts_status",
            schema: "integrations", table: "provider_execution_receipts");
        migrationBuilder.AddCheckConstraint(name: "ck_provider_execution_receipts_status",
            schema: "integrations", table: "provider_execution_receipts",
            sql: "status IN ('Pending','Completed','Released','Ambiguous')");
        migrationBuilder.AddCheckConstraint(name: "ck_provider_execution_receipts_lifecycle",
            schema: "integrations", table: "provider_execution_receipts",
            sql: "(status = 'Pending' AND result_json IS NULL AND completed_at IS NULL) OR "
                + "(status IN ('Completed','Released','Ambiguous') AND result_json IS NOT NULL AND completed_at >= created_at)");
        migrationBuilder.AddCheckConstraint(name: "ck_provider_execution_receipts_costs",
            schema: "integrations", table: "provider_execution_receipts",
            sql: "reserved_cost_usd >= 0 AND (actual_cost_usd IS NULL OR actual_cost_usd >= 0)");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropCheckConstraint(name: "ck_provider_execution_receipts_costs",
            schema: "integrations", table: "provider_execution_receipts");
        migrationBuilder.DropCheckConstraint(name: "ck_provider_execution_receipts_lifecycle",
            schema: "integrations", table: "provider_execution_receipts");
        migrationBuilder.DropCheckConstraint(name: "ck_provider_execution_receipts_status",
            schema: "integrations", table: "provider_execution_receipts");
        migrationBuilder.Sql("""
            DO $$
            BEGIN
                IF EXISTS (SELECT 1 FROM integrations.provider_execution_receipts
                           WHERE status NOT IN ('Pending','Completed')) THEN
                    RAISE EXCEPTION 'Cannot downgrade while released or ambiguous provider receipts exist.';
                END IF;
            END $$;
            """);
        migrationBuilder.AddCheckConstraint(name: "ck_provider_execution_receipts_status",
            schema: "integrations", table: "provider_execution_receipts", sql: "status IN ('Pending','Completed')");
        migrationBuilder.AddCheckConstraint(name: "ck_provider_execution_receipts_lifecycle",
            schema: "integrations", table: "provider_execution_receipts",
            sql: "(status = 'Pending' AND result_json IS NULL AND completed_at IS NULL) OR "
                + "(status = 'Completed' AND result_json IS NOT NULL AND completed_at >= created_at)");
        migrationBuilder.DropColumn(name: "actual_cost_usd", schema: "integrations", table: "provider_execution_receipts");
        migrationBuilder.DropColumn(name: "provider", schema: "integrations", table: "provider_execution_receipts");
        migrationBuilder.DropColumn(name: "reserved_cost_usd", schema: "integrations", table: "provider_execution_receipts");
    }
}
